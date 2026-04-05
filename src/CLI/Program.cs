using System.Diagnostics;
using System.Text;
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
internal partial class Program
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

        string command = args[0]
                        .ToLowerInvariant()
                        .TrimStart(trimChar: '-');

        // Check if first arg is a command or a file
        bool isCommand = command == "parse" || command == "tokenize" || command == "codegen" ||
                         command == "emit" || command == "build" || command == "buildandrun" ||
                         command == "check" || command == "validate-stdlib" || command == "help";

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
                    Console.WriteLine(value: "Error: parse command requires a file path");
                    return 1;
                }

                return ParseFile(sourceFile: args[1]);

            case "tokenize":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: tokenize command requires a file path");
                    return 1;
                }

                return TokenizeFile(sourceFile: args[1]);

            case "codegen":
            case "emit":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: codegen command requires a file path");
                    return 1;
                }

                return GenerateCode(sourceFile: args[1],
                    outputFile: args.Length > 2
                        ? args[2]
                        : null,
                    buildMode: RfBuildMode.Debug);

            case "build":
            {
                (string? entryFile, string? projectRoot, string? outputFile2,
                    RfBuildMode buildMode2) = ResolveEntryFile(args: args, needsOutputArg: true);
                if (entryFile == null)
                {
                    return 1;
                }

                return BuildMultiFile(entryFile: entryFile,
                    outputFile: outputFile2,
                    projectRoot: projectRoot,
                    buildMode: buildMode2);
            }

            case "buildandrun":
            {
                (string? entryFile, string? projectRoot, _,
                    RfBuildMode buildMode3) = ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                return BuildAndRun(entryFile: entryFile,
                    projectRoot: projectRoot,
                    buildMode: buildMode3);
            }

            case "check":
            {
                (string? entryFile, string? projectRoot, _, _) =
                    ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                return CheckMultiFile(entryFile: entryFile, projectRoot: projectRoot);
            }

            case "validate-stdlib":
            {
                string lang = args.Length >= 2
                    ? args[1]
                       .ToLowerInvariant()
                    : "rf";
                Language stdlibLang = lang == "sf" || lang == "suflae"
                    ? Language.Suflae
                    : Language.RazorForge;
                return ValidateStdlib(language: stdlibLang);
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
    /// Resolves the entry file, project root, optional output file, and build mode
    /// for build/buildandrun/check commands.
    /// When no explicit entry file is given, searches for a razorforge.toml manifest.
    /// Supports --target to select a specific target from the manifest.
    /// Returns (entryFile, projectRoot, outputFile, buildMode); entryFile is null on error.
    /// </summary>
    private static (string? EntryFile, string? ProjectRoot, string? OutputFile,
        RfBuildMode BuildMode) ResolveEntryFile(string[] args, bool needsOutputArg)
    {
        // args[0] is the command name (build/buildandrun/check)
        string? targetName = null;
        string? explicitEntry = null;
        string? outputFile = null;

        // Parse remaining args
        int i = 1;
        while (i < args.Length)
        {
            if (args[i] == "--target" && i + 1 < args.Length)
            {
                targetName = args[i + 1];
                i += 2;
            }
            else if (!args[i]
                        .StartsWith(value: "-"))
            {
                if (explicitEntry == null)
                {
                    explicitEntry = args[i];
                }
                else if (needsOutputArg && outputFile == null)
                {
                    outputFile = args[i];
                }

                i++;
            }
            else
            {
                i++;
            }
        }

        // Explicit entry file given — use it directly (debug mode, no manifest)
        if (explicitEntry != null)
        {
            if (!File.Exists(path: explicitEntry))
            {
                Console.WriteLine(value: $"Error: File '{explicitEntry}' not found.");
                return (null, null, null, RfBuildMode.Debug);
            }

            string projectRoot =
                Path.GetDirectoryName(path: Path.GetFullPath(path: explicitEntry)) ?? ".";
            return (explicitEntry, projectRoot, outputFile, RfBuildMode.Debug);
        }

        // No explicit entry — search for manifest
        string? manifestPath = ManifestLoader.FindManifest(startDir: Environment.CurrentDirectory);
        if (manifestPath == null)
        {
            Console.WriteLine(
                value: "Error: No entry file specified and no razorforge.toml found.");
            Console.WriteLine(
                value: "Either provide an entry file or create a razorforge.toml manifest.");
            return (null, null, null, RfBuildMode.Debug);
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath);

            TargetInfo? target;
            if (targetName != null)
            {
                target = manifest.Targets.Find(match: t => string.Equals(a: t.Name,
                    b: targetName,
                    comparisonType: StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    Console.WriteLine(
                        value:
                        $"Error: Target '{targetName}' not found in {ManifestLoader.ManifestFileName}.");
                    Console.WriteLine(
                        value:
                        $"Available targets: {string.Join(separator: ", ", values: manifest.Targets.Select(selector: t => t.Name))}");
                    return (null, null, null, RfBuildMode.Debug);
                }
            }
            else
            {
                // Use first executable target, or first target if none is executable
                target = manifest.Targets.Find(match: t => t.Type == "executable") ??
                         manifest.Targets[index: 0];
            }

            RfBuildMode buildMode = target.Mode.ToLowerInvariant() switch
            {
                "release" => RfBuildMode.Release,
                "release-time" => RfBuildMode.ReleaseTime,
                "release-space" => RfBuildMode.ReleaseSpace,
                _ => RfBuildMode.Debug
            };

            Console.WriteLine(value: $"Using manifest: {manifestPath}");
            Console.WriteLine(value: $"Target: {target.Name} ({target.Type}, {target.Mode})");

            return (target.Entry, manifest.ManifestDirectory, outputFile, buildMode);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return (null, null, null, RfBuildMode.Debug);
        }
    }

    /// <summary>
    /// Prints the CLI usage instructions to standard output.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine(value: "RazorForge Builder");
        Console.WriteLine();
        Console.WriteLine(value: "Usage:");
        Console.WriteLine(
            value:
            "  RazorForge <source-file>                        - Parse file and show AST summary");
        Console.WriteLine(
            value:
            "  RazorForge parse <source-file>                  - Parse file and show AST summary");
        Console.WriteLine(
            value:
            "  RazorForge tokenize <source-file>               - Tokenize file and show tokens");
        Console.WriteLine(
            value:
            "  RazorForge codegen <source-file> [out.ll]       - Generate LLVM IR (single file)");
        Console.WriteLine(
            value: "  RazorForge build [entry-file] [out.ll]          - Build multi-file project");
        Console.WriteLine(
            value:
            "  RazorForge build --target <name> [out.ll]       - Build a specific manifest target");
        Console.WriteLine(
            value: "  RazorForge buildandrun [entry-file]             - Build and execute");
        Console.WriteLine(
            value:
            "  RazorForge buildandrun --target <name>          - Build and execute manifest target");
        Console.WriteLine(
            value:
            "  RazorForge check [entry-file]                   - Type-check only (no codegen)");
        Console.WriteLine(
            value:
            "  RazorForge check --target <name>                - Type-check manifest target");
        Console.WriteLine(
            value:
            "  RazorForge validate-stdlib [rf|sf]              - Validate stdlib routine bodies");
        Console.WriteLine(
            value: "  RazorForge help                                 - Show this help");
        Console.WriteLine();
        Console.WriteLine(
            value: "  <source-file>: .rf file for RazorForge or .sf file for Suflae");
        Console.WriteLine(
            value: "  If no entry file is given, searches for razorforge.toml in the current");
        Console.WriteLine(value: "  directory and parent directories.");
    }

    /// <summary>Returns true if the given file path has a <c>.sf</c> extension (Suflae source file).</summary>
    private static bool IsSuflaeFile(string path)
    {
        return path.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tokenizes the given source file and prints each token with its position and text to standard output.
    /// Returns 0 on success or 1 if the file is not found or tokenization fails.
    /// </summary>
    private static int TokenizeFile(string sourceFile)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        bool isSuflae = IsSuflaeFile(path: sourceFile);

        Console.WriteLine(
            value: $"Tokenizing {sourceFile} as {(isSuflae ? "Suflae" : "RazorForge")}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;
            var tokenizer = new Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();

            Console.WriteLine(value: $"Generated {tokens.Count} tokens:");
            Console.WriteLine();

            foreach (Token tok in tokens)
            {
                Console.WriteLine(
                    value:
                    $"  {tok.Line,4}:{tok.Column,-3} {tok.Type,-25} '{EscapeString(s: tok.Text)}'");
            }

            Console.WriteLine();
            Console.WriteLine(value: "Tokenization successful!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Tokenization failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Tokenizes and parses the given source file, then prints a summary of the resulting AST
    /// along with any warnings. Returns 0 on success or 1 if the file is not found or parsing fails.
    /// </summary>
    private static int ParseFile(string sourceFile)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        bool isSuflae = IsSuflaeFile(path: sourceFile);

        Console.WriteLine(
            value: $"Parsing {sourceFile} as {(isSuflae ? "Suflae" : "RazorForge")}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            var tokenizer = new Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            IReadOnlyList<BuildWarning> warnings = parser.GetWarnings();

            Console.WriteLine(
                value: $"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Show warnings if any
            if (warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== WARNINGS ({warnings.Count}) ===");
                foreach (BuildWarning warning in warnings)
                {
                    Console.WriteLine(
                        value: $"  [{warning.Line}:{warning.Column}] {warning.Message}");
                }
            }

            // Show AST summary
            Console.WriteLine();
            Console.WriteLine(value: "=== AST SUMMARY ===");
            foreach (IAstNode decl in ast.Declarations)
            {
                PrintDeclarationSummary(node: decl, indent: 0);
            }

            Console.WriteLine();
            Console.WriteLine(value: "Parsing successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine(value: ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: ex.Message);
            Console.WriteLine(value: ex.StackTrace);
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
            string langName = language == Language.Suflae
                ? "Suflae"
                : "RazorForge";
            Console.WriteLine(value: $"Validating {langName} stdlib routine bodies...");
            Console.WriteLine();

            var analyzer = new SemanticAnalyzer(language: language);
            IReadOnlyList<SemanticError> stdlibErrors = analyzer.ValidateStdlibBodies();

            if (stdlibErrors.Count == 0)
            {
                Console.WriteLine(value: "All stdlib routine bodies validated successfully!");
                return 0;
            }

            // Group errors by file
            var errorsByFile = new Dictionary<string, List<SemanticError>>();
            foreach (SemanticError error in stdlibErrors)
            {
                string file = error.Location.FileName;
                if (!errorsByFile.TryGetValue(key: file, value: out List<SemanticError>? list))
                {
                    list = [];
                    errorsByFile[key: file] = list;
                }

                list.Add(item: error);
            }

            Console.WriteLine(
                value:
                $"=== STDLIB VALIDATION ERRORS ({stdlibErrors.Count} errors in {errorsByFile.Count} files) ===");
            Console.WriteLine();

            foreach ((string file, List<SemanticError> errors) in errorsByFile.OrderBy(
                         keySelector: kvp => kvp.Key))
            {
                Console.WriteLine(
                    value: $"  {Path.GetFileName(path: file)} ({errors.Count} errors):");
                foreach (SemanticError error in errors)
                {
                    Console.WriteLine(value: $"    {error.FormattedMessage}");
                }

                Console.WriteLine();
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Stdlib validation failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the full compiler pipeline (tokenize ??parse ??semantic analysis ??LLVM IR generation)
    /// on the given source file and writes the resulting IR to <paramref name="outputFile"/>,
    /// or to a default <c>.ll</c> file if no output path is specified.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int GenerateCode(string sourceFile, string? outputFile,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        bool isSuflae = IsSuflaeFile(path: sourceFile);

        Console.WriteLine(
            value: $"Building {sourceFile} as {(isSuflae ? "Suflae" : "RazorForge")}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            var tokenizer = new Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            IReadOnlyList<BuildWarning> parseWarnings = parser.GetWarnings();

            Console.WriteLine(value: $"Parsed {ast.Declarations.Count} declarations");

            // Semantic Analysis
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticAnalyzer(language: language);
            AnalysisResult result = analyzer.Analyze(program: ast);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            // Show errors and warnings
            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                foreach (SemanticError error in result.Errors)
                {
                    Console.WriteLine(value: $"  {error.FormattedMessage}");
                }

                Console.WriteLine();
                Console.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                foreach (SemanticWarning warning in result.Warnings)
                {
                    Console.WriteLine(value: $"  {warning.FormattedMessage}");
                }
            }

            // Code Generation
            Console.WriteLine();
            Console.WriteLine(value: "=== CODE GENERATION ===");

            // Pass stdlib programs to codegen so intrinsic routines get built
            IReadOnlyList<(SyntaxTree.Program Program, string FilePath, string Module)>
                stdlibPrograms = result.Registry.StdlibPrograms;
            var generator = new LLVMCodeGenerator(program: ast,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                buildMode: buildMode);
            string llvmIR = generator.Generate();

            // Output
            if (outputFile != null)
            {
                File.WriteAllText(path: outputFile, contents: llvmIR);
                Console.WriteLine(value: $"LLVM IR written to: {outputFile}");
            }
            else
            {
                // Default output file
                string defaultOutput = Path.ChangeExtension(path: sourceFile, extension: ".ll");
                File.WriteAllText(path: defaultOutput, contents: llvmIR);
                Console.WriteLine(value: $"LLVM IR written to: {defaultOutput}");
            }

            Console.WriteLine();
            Console.WriteLine(value: "Code generation successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine(value: $"{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Build failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Rebuilds the native runtime library via cmake --build.
    /// Returns 0 on success or 1 if the build fails.
    /// </summary>
    private static int BuildNativeRuntime()
    {
        string? exeDir = Path.GetDirectoryName(path: typeof(Program).Assembly.Location);

        // Find native/build by walking up from the executable directory
        string? current = exeDir;
        string? nativeBuildDir = null;
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidate = Path.Combine(path1: current, path2: "native", path3: "build");
            if (File.Exists(path: Path.Combine(path1: candidate, path2: "build.ninja")) ||
                File.Exists(path: Path.Combine(path1: candidate, path2: "Makefile")))
            {
                nativeBuildDir = candidate;
                break;
            }

            current = Path.GetDirectoryName(path: current);
        }

        if (nativeBuildDir == null)
        {
            return 0; // No native build directory found ??skip silently
        }

        var psi = new ProcessStartInfo
        {
            FileName = "cmake",
            Arguments = $"--build \"{nativeBuildDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Warning: Failed to start cmake.");
                return 0; // Non-fatal ??continue with existing runtime
            }

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.Error.Write(value: stderr);
                Console.WriteLine(
                    value:
                    $"Native runtime build failed (cmake exited with code {process.ExitCode})");
                return 1;
            }

            // Copy fresh artifacts to the exe directory so they're picked up at link/run time
            if (exeDir != null)
            {
                string nativeBinDir = Path.Combine(path1: nativeBuildDir, path2: "bin");
                string nativeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");
                string exeNativeBinDir = Path.Combine(path1: exeDir,
                    path2: "native",
                    path3: "build",
                    path4: "bin");
                string exeNativeLibDir = Path.Combine(path1: exeDir,
                    path2: "native",
                    path3: "build",
                    path4: "lib");

                CopyDirectoryFiles(srcDir: nativeBinDir, dstDir: exeNativeBinDir);
                CopyDirectoryFiles(srcDir: nativeLibDir, dstDir: exeNativeLibDir);

                // Also copy DLLs to the exe root (matches csproj LinkBase="." behavior)
                if (Directory.Exists(path: nativeBinDir))
                {
                    foreach (string dll in Directory.GetFiles(path: nativeBinDir,
                                 searchPattern: "*.dll"))
                    {
                        File.Copy(sourceFileName: dll,
                            destFileName: Path.Combine(path1: exeDir,
                                path2: Path.GetFileName(path: dll)),
                            overwrite: true);
                    }
                }
            }

            return 0;
        }
        catch (Exception)
        {
            return 0; // cmake not found ??skip silently
        }
    }

    private static void CopyDirectoryFiles(string srcDir, string dstDir)
    {
        if (!Directory.Exists(path: srcDir))
        {
            return;
        }

        Directory.CreateDirectory(path: dstDir);
        foreach (string file in Directory.GetFiles(path: srcDir))
        {
            File.Copy(sourceFileName: file,
                destFileName: Path.Combine(path1: dstDir, path2: Path.GetFileName(path: file)),
                overwrite: true);
        }
    }

    /// <summary>
    /// Detects the underlying linker tool name from clang's stderr output.
    /// </summary>
    private static string DetectLinkerFromStderr(string stderr)
    {
        if (stderr.Contains(value: "lld-link:"))
        {
            return "lld-link";
        }

        if (stderr.Contains(value: "ld.lld:"))
        {
            return "ld.lld";
        }

        if (stderr.Contains(value: "collect2:"))
        {
            return "collect2";
        }

        if (stderr.Contains(value: "LINK :") || stderr.Contains(value: "LINK:"))
        {
            return "link.exe";
        }

        if (stderr.Contains(value: "ld:"))
        {
            return "ld";
        }

        return "clang";
    }

    /// <summary>
    /// Runs the multi-file build pipeline: BuildDriver (parse + resolve imports + topo sort)
    /// ??SemanticAnalyzer.AnalyzeMultiple ??LLVMCodeGenerator with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile,
        string? projectRoot = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        if (!File.Exists(path: entryFile))
        {
            Console.WriteLine(value: $"Error: File '{entryFile}' not found.");
            return 1;
        }

        // Rebuild native runtime if sources changed
        int nativeResult = BuildNativeRuntime();
        if (nativeResult != 0)
        {
            return nativeResult;
        }

        bool isSuflae = IsSuflaeFile(path: entryFile);
        Language language = isSuflae
            ? Language.Suflae
            : Language.RazorForge;

        Console.WriteLine(
            value:
            $"Building {entryFile} as {(isSuflae ? "Suflae" : "RazorForge")} (multi-file)...");
        Console.WriteLine();

        try
        {
            // Use provided project root (from manifest) or fall back to entry file directory
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                foreach (SemanticError error in buildResult.Errors)
                {
                    Console.WriteLine(value: $"  {error.FormattedMessage}");
                }

                Console.WriteLine();
                Console.WriteLine(value: "Build aborted due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
                foreach (BuildWarning warning in buildResult.Warnings)
                {
                    Console.WriteLine(
                        value: $"  [{warning.Line}:{warning.Column}] {warning.Message}");
                }
            }

            Console.WriteLine(
                value:
                $"Initialization order: {string.Join(separator: " -> ", values: buildResult
                .InitializationOrder)}");

            // Filter out stdlib files they are already loaded by TypeRegistry/StdlibLoader
            string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
            var userUnits = buildResult.Units
                                       .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                           .StartsWith(value: normalizedStdlib,
                                                comparisonType: StringComparison
                                                   .OrdinalIgnoreCase))
                                       .ToList();

            // Build file list in topological order
            var unitsByFile =
                new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (FileBuildUnit unit in userUnits)
            {
                unitsByFile[key: unit.FilePath] = unit;
            }

            // Map module names back to file units for ordering
            var unitsByModule =
                new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (FileBuildUnit unit in userUnits)
            {
                string moduleName =
                    unit.Module ?? Path.GetFileNameWithoutExtension(path: unit.FilePath);
                unitsByModule[key: moduleName] = unit;
            }

            var orderedFiles = new List<(SyntaxTree.Program Program, string FilePath)>();
            foreach (string moduleName in buildResult.InitializationOrder)
            {
                if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            // Fallback: if init order doesn't cover all units (e.g., entry file with no module decl)
            foreach (FileBuildUnit unit in userUnits)
            {
                if (!orderedFiles.Any(predicate: f => string.Equals(a: f.FilePath,
                        b: unit.FilePath,
                        comparisonType: StringComparison.OrdinalIgnoreCase)))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            // Phase 2: Semantic analysis (multi-file)
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticAnalyzer(language: language);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                foreach (SemanticError error in result.Errors)
                {
                    Console.WriteLine(value: $"  {error.FormattedMessage}");
                }

                Console.WriteLine();
                Console.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                foreach (SemanticWarning warning in result.Warnings)
                {
                    Console.WriteLine(value: $"  {warning.FormattedMessage}");
                }
            }

            // Phase 3: Code generation (multi-program)
            Console.WriteLine();
            Console.WriteLine(value: "=== CODE GENERATION ===");

            var userPrograms = orderedFiles.Select(selector: f =>
                                            {
                                                string module =
                                                    unitsByFile.TryGetValue(key: f.FilePath,
                                                        value: out FileBuildUnit? u)
                                                        ? u.Module ?? ""
                                                        : "";
                                                return (f.Program, f.FilePath, module);
                                            })
                                           .ToList();

            IReadOnlyList<(SyntaxTree.Program Program, string FilePath, string Module)>
                stdlibPrograms = result.Registry.StdlibPrograms;
            var generator = new LLVMCodeGenerator(userPrograms: userPrograms,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                buildMode: buildMode);
            string llvmIR = generator.Generate();

            // Output
            string outPath = outputFile ?? Path.ChangeExtension(path: entryFile, extension: ".ll");
            File.WriteAllText(path: outPath, contents: llvmIR);
            Console.WriteLine(value: $"LLVM IR written to: {outPath}");

            Console.WriteLine();
            Console.WriteLine(value: "Build successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine(value: $"{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Build failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the multi-file build pipeline through semantic analysis only (no codegen).
    /// Reports errors and warnings. Returns 0 if type-checking succeeds, 1 otherwise.
    /// </summary>
    private static int CheckMultiFile(string entryFile, string? projectRoot = null)
    {
        if (!File.Exists(path: entryFile))
        {
            Console.WriteLine(value: $"Error: File '{entryFile}' not found.");
            return 1;
        }

        bool isSuflae = IsSuflaeFile(path: entryFile);
        Language language = isSuflae
            ? Language.Suflae
            : Language.RazorForge;

        Console.WriteLine(
            value:
            $"Checking {entryFile} as {(isSuflae ? "Suflae" : "RazorForge")} (multi-file)...");
        Console.WriteLine();

        try
        {
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                foreach (SemanticError error in buildResult.Errors)
                {
                    Console.WriteLine(value: $"  {error.FormattedMessage}");
                }

                Console.WriteLine();
                Console.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
                foreach (BuildWarning warning in buildResult.Warnings)
                {
                    Console.WriteLine(
                        value: $"  [{warning.Line}:{warning.Column}] {warning.Message}");
                }
            }

            // Filter out stdlib files
            string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
            var userUnits = buildResult.Units
                                       .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                           .StartsWith(value: normalizedStdlib,
                                                comparisonType: StringComparison
                                                   .OrdinalIgnoreCase))
                                       .ToList();

            var unitsByModule =
                new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (FileBuildUnit unit in userUnits)
            {
                string moduleName =
                    unit.Module ?? Path.GetFileNameWithoutExtension(path: unit.FilePath);
                unitsByModule[key: moduleName] = unit;
            }

            var orderedFiles = new List<(SyntaxTree.Program Program, string FilePath)>();
            foreach (string moduleName in buildResult.InitializationOrder)
            {
                if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            foreach (FileBuildUnit unit in userUnits)
            {
                if (!orderedFiles.Any(predicate: f => string.Equals(a: f.FilePath,
                        b: unit.FilePath,
                        comparisonType: StringComparison.OrdinalIgnoreCase)))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            // Phase 2: Semantic analysis (multi-file) ??no codegen
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticAnalyzer(language: language);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                foreach (SemanticError error in result.Errors)
                {
                    Console.WriteLine(value: $"  {error.FormattedMessage}");
                }

                Console.WriteLine();
                Console.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                foreach (SemanticWarning warning in result.Warnings)
                {
                    Console.WriteLine(value: $"  {warning.FormattedMessage}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(value: "Check passed!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine(value: $"{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Check failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Builds a multi-file project and executes the resulting LLVM IR via lli.
    /// Returns 0 on success or 1 if build or execution fails.
    /// </summary>
    private static int BuildAndRun(string entryFile, string? projectRoot = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        // Remove stale per-target outputs before rebuilding.
        string llFile = Path.ChangeExtension(path: entryFile, extension: ".ll");
        string optFile = Path.ChangeExtension(path: llFile, extension: ".opt.ll");
        string exeFile = Path.ChangeExtension(path: llFile, extension: ".exe");
        CleanBuildAndRunOutputs(llFile: llFile, optFile: optFile, exeFile: exeFile);

        // Build first (to a temp .ll file)
        int buildResult = BuildMultiFile(entryFile: entryFile,
            outputFile: llFile,
            projectRoot: projectRoot,
            buildMode: buildMode);
        if (buildResult != 0)
        {
            return buildResult;
        }

        // Find the runtime import library (.lib) directory
        string? exeDir = Path.GetDirectoryName(path: typeof(Program).Assembly.Location);
        string runtimeLibDir = Path.Combine(path1: exeDir ?? ".",
            path2: "native",
            path3: "build",
            path4: "lib");

        string optPipelineLevel = buildMode switch
        {
            RfBuildMode.Release => "O2",
            RfBuildMode.ReleaseTime => "O3",
            RfBuildMode.ReleaseSpace => "Os",
            _ => "O0"
        };

        // Debug: run mem2reg+sroa at O0 (improves readability without changing semantics).
        // Optimized builds: run the full pipeline at the requested level (includes mem2reg+sroa).
        // Use -passes='default<Ox>,...' syntax (LLVM 14+; replaces the -Ox -passes=... split form).
        string optPipeline = buildMode == RfBuildMode.Debug
            ? $"default<{optPipelineLevel}>,mem2reg,sroa"
            : $"default<{optPipelineLevel}>";
        string optArgs = $"-S -passes={optPipeline} \"{llFile}\" -o \"{optFile}\"";
        var optPsi = new ProcessStartInfo
        {
            FileName = "opt",
            Arguments = optArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var optProcess = Process.Start(startInfo: optPsi);
            if (optProcess != null)
            {
                string optStderr = optProcess.StandardError.ReadToEnd();
                optProcess.WaitForExit();

                if (optProcess.ExitCode != 0)
                {
                    // opt failed — fall back to unoptimized IR
                    Console.Error.WriteLine(value: $"opt warning: {optStderr.Trim()}");
                    optFile = llFile;
                }
            }
            else
            {
                optFile = llFile;
            }
        }
        catch
        {
            // opt not available — fall back to unoptimized IR
            optFile = llFile;
        }

        // Compile .ll → .exe using clang (clang uses -Ox flag style, not opt's -passes= form)
        string clangOptLevel = $"-{optPipelineLevel}";
        string windowsThreadingLibs = OperatingSystem.IsWindows()
            ? " -lucrt -lmsvcrt -lkernel32"
            : "";
        string clangArgs =
            $"{clangOptLevel} -o \"{exeFile}\" \"{optFile}\" -L\"{runtimeLibDir}\" -lrazorforge_runtime{windowsThreadingLibs}";

        var clangPsi = new ProcessStartInfo
        {
            FileName = "clang",
            Arguments = clangArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var clangProcess = Process.Start(startInfo: clangPsi);
            if (clangProcess == null)
            {
                Console.WriteLine(value: "Error: Failed to start clang.");
                return 1;
            }

            string clangStdout = clangProcess.StandardOutput.ReadToEnd();
            string clangStderr = clangProcess.StandardError.ReadToEnd();
            clangProcess.WaitForExit();

            if (clangProcess.ExitCode != 0)
            {
                // MSVC's link.exe sends detailed errors (LNK2019) to stdout,
                // while the summary (LNK1120) goes to stderr ??print both.
                if (!string.IsNullOrWhiteSpace(value: clangStdout))
                {
                    Console.Error.Write(value: clangStdout);
                }

                if (!string.IsNullOrWhiteSpace(value: clangStderr))
                {
                    Console.Error.Write(value: clangStderr);
                }

                string allOutput = clangStdout + clangStderr;
                string linker = DetectLinkerFromStderr(stderr: allOutput);
                Console.WriteLine(
                    value: $"Linking failed ({linker} exited with code {clangProcess.ExitCode})");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute clang: {ex.Message}");
            Console.WriteLine(
                value: "Make sure LLVM/Clang is installed and 'clang' is on your PATH.");
            return 1;
        }

        // Copy the runtime DLL next to the output .exe so it can be found at runtime
        string? outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile));
        if (outputDir != null && exeDir != null)
        {
            string srcDll = Path.Combine(path1: exeDir, path2: "razorforge_runtime.dll");
            if (File.Exists(path: srcDll))
            {
                string dstDll = Path.Combine(path1: outputDir, path2: "razorforge_runtime.dll");
                File.Copy(sourceFileName: srcDll, destFileName: dstDll, overwrite: true);
            }
        }

        // Run the produced .exe
        Console.WriteLine();
        Console.WriteLine(value: "=== EXECUTION ===");

        bool stdinIsPiped = Console.IsInputRedirected;
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(path: exeFile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinIsPiped
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Error: Failed to start the compiled executable.");
                return 1;
            }

            if (stdinIsPiped)
            {
                Console.OpenStandardInput()
                       .CopyTo(destination: process.StandardInput.BaseStream);
                process.StandardInput.Close();
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(value: stdout))
            {
                Console.Write(value: stdout);
            }

            if (!string.IsNullOrEmpty(value: stderr))
            {
                Console.Error.Write(value: stderr);
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute {exeFile}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Deletes stale per-target outputs that can cause buildandrun to execute or link against
    /// previous artifacts after source, stdlib, or runtime changes.
    /// </summary>
    private static void CleanBuildAndRunOutputs(string llFile, string optFile, string exeFile)
    {
        string basePath = Path.Combine(
            path1: Path.GetDirectoryName(path: exeFile) ?? ".",
            path2: Path.GetFileNameWithoutExtension(path: exeFile));

        string[] pathsToDelete =
        [
            llFile,
            optFile,
            exeFile,
            basePath + ".obj",
            basePath + ".pdb",
            basePath + ".ilk",
            basePath + ".exp",
            basePath + ".lib",
            Path.Combine(path1: Path.GetDirectoryName(path: exeFile) ?? ".",
                path2: "razorforge_runtime.dll")
        ];

        foreach (string path in pathsToDelete.Distinct(comparer: StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path: path))
                {
                    File.Delete(path: path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine(value: $"Warning: Could not remove stale build artifact '{path}': {ex.Message}");
            }
        }
    }

}
