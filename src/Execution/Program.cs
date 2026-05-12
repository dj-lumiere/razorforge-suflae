using System.Diagnostics;
using Compiler.CodeGen;
using Compiler.Declaration;
using Compiler.Diagnostics;
using Compiler.Lexer;
using Compiler.Parser;
using Compiler.Targeting;
using Verification;
using Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder;

/// <summary>
/// Command-line entry point for the RazorForge compiler toolchain.
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
        RuntimeShadowLoader.Install();

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0]
                        .ToLowerInvariant()
                        .TrimStart(trimChar: '-');

        // Check if first arg is a command or a file
        bool isCommand = command is "parse" or "tokenize" or "codegen" or "build" or "buildandrun" or "cleanbuildandrun" or "check" or "validate-stdlib" or "help";

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
                    RfBuildMode buildMode2, bool dumpAst2, bool saTiming2, bool requireStart2) = ResolveEntryFile(args: args, needsOutputArg: true);
                if (entryFile == null)
                {
                    return 1;
                }

                return BuildMultiFile(entryFile: entryFile,
                    outputFile: outputFile2,
                    projectRoot: projectRoot,
                    buildMode: buildMode2,
                    dumpAst: dumpAst2,
                    saTiming: saTiming2,
                    requireStartRoutine: requireStart2);
            }

            case "buildandrun":
            {
                (string? entryFile, string? projectRoot, _,
                    RfBuildMode buildMode3, bool dumpAst3, bool saTiming3, bool requireStart3) = ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                return BuildAndRun(entryFile: entryFile,
                    projectRoot: projectRoot,
                    buildMode: buildMode3,
                    dumpAst: dumpAst3,
                    saTiming: saTiming3,
                    cleanNativeRuntime: false,
                    requireStartRoutine: requireStart3);
            }

            case "cleanbuildandrun":
            {
                // Stage 1: rebuild the C# compiler project, then re-exec the freshly-built
                // exe so this run actually uses the new compiler. The stage-2 env var stops
                // the spawned child from looping back into another rebuild.
                if (Environment.GetEnvironmentVariable(variable: CleanBuildAndRunStage2EnvVar)
                    != "1")
                {
                    int compilerRc = RebuildCompilerProject();
                    if (compilerRc != 0) return compilerRc;
                    return ReExecCleanBuildAndRun(args: args);
                }

                // Stage 2: running inside the freshly-built compiler. Do a clean native
                // runtime rebuild and then the normal buildandrun.
                (string? entryFile, string? projectRoot, _,
                    RfBuildMode buildMode4, bool dumpAst4, bool saTiming4, bool requireStart4) = ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                return BuildAndRun(entryFile: entryFile,
                    projectRoot: projectRoot,
                    buildMode: buildMode4,
                    dumpAst: dumpAst4,
                    saTiming: saTiming4,
                    cleanNativeRuntime: true,
                    requireStartRoutine: requireStart4);
            }

            case "check":
            {
                (string? entryFile, string? projectRoot, _, _, _, _, _) =
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
                Language stdlibLang = lang is "sf" or "suflae"
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
    /// Resolves the entry file, project root, optional output file, build mode, dump-ast, and sa-timing flags
    /// for build/buildandrun/check commands.
    /// When no explicit entry file is given, searches for a razorforge.toml manifest.
    /// Supports --target to select a specific target from the manifest.
    /// Returns (entryFile, projectRoot, outputFile, buildMode, dumpAst, saTiming); entryFile is null on error.
    /// </summary>
    private static (string? EntryFile, string? ProjectRoot, string? OutputFile,
        RfBuildMode BuildMode, bool DumpAst, bool SaTiming, bool RequireStartRoutine) ResolveEntryFile(string[] args, bool needsOutputArg)
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

        // Explicit source file given — use it directly (debug mode, no manifest)
        // .toml files are treated as manifests, not source files
        if (explicitEntry != null &&
            !explicitEntry.EndsWith(value: ".toml", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path: explicitEntry))
            {
                Console.WriteLine(value: $"Error: File '{explicitEntry}' not found.");
                return (null, null, null, RfBuildMode.Debug, false, false, false);
            }

            string projectRoot =
                Path.GetDirectoryName(path: Path.GetFullPath(path: explicitEntry)) ?? ".";
            // Bare .rf source given without manifest — assume an executable build so codegen
            // knows to synthesize @main and SA can require a 'start' routine.
            return (explicitEntry, projectRoot, outputFile, RfBuildMode.Debug, false, false, true);
        }

        // No explicit entry (or .toml manifest given) — load manifest
        string? manifestPath = explicitEntry != null
            ? (File.Exists(path: explicitEntry)
                ? Path.GetFullPath(path: explicitEntry)
                : null)
            : ManifestLoader.FindManifest(startDir: Environment.CurrentDirectory);
        if (manifestPath == null)
        {
            if (explicitEntry != null)
            {
                Console.WriteLine(value: $"Error: Manifest '{explicitEntry}' not found.");
            }
            else
            {
                Console.WriteLine(
                    value: "Error: No entry file specified and no razorforge.toml found.");
                Console.WriteLine(
                    value: "Either provide an entry file or create a razorforge.toml manifest.");
            }

            return (null, null, null, RfBuildMode.Debug, false, false, false);
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath);
            TargetInfo target = ResolveManifestTarget(manifest: manifest, targetName: targetName);

            RfBuildMode buildMode = target.Mode.ToLowerInvariant() switch
            {
                "debug" => RfBuildMode.Debug,
                "release" => RfBuildMode.Release,
                "release-time" => RfBuildMode.ReleaseTime,
                "release-space" => RfBuildMode.ReleaseSpace,
                _ => throw new InvalidOperationException(
                    $"Unknown build mode '{target.Mode}' in manifest target '{target.Name}'. " +
                    "Valid modes are: debug, release, release-time, release-space.")
            };

            Console.WriteLine(value: $"Using manifest: {manifestPath}");
            Console.WriteLine(value: $"Target: {target.Name} ({target.Type}, {target.Mode})");

            bool requireStartRoutine = string.Equals(a: target.Type,
                b: "executable",
                comparisonType: StringComparison.OrdinalIgnoreCase);
            return (target.Entry, manifest.ManifestDirectory, outputFile, buildMode, target.DumpAst, target.SaTiming, requireStartRoutine);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return (null, null, null, RfBuildMode.Debug, false, false, false);
        }
    }

    private static TargetInfo ResolveManifestTarget(ProjectManifest manifest, string? targetName)
    {
        if (targetName != null)
        {
            TargetInfo? explicitTarget = manifest.Targets.Find(match: t => string.Equals(a: t.Name,
                b: targetName,
                comparisonType: StringComparison.OrdinalIgnoreCase));
            if (explicitTarget == null)
            {
                throw new InvalidOperationException(
                    $"Target '{targetName}' not found in {ManifestLoader.ManifestFileName}. " +
                    $"Available targets: {string.Join(separator: ", ", values: manifest.Targets.Select(selector: t => t.Name))}");
            }

            return explicitTarget;
        }

        if (manifest.Targets.Count == 1)
        {
            return manifest.Targets[index: 0];
        }

        var executableTargets = manifest.Targets
                                        .Where(predicate: t => string.Equals(a: t.Type,
                                             b: "executable",
                                             comparisonType: StringComparison.OrdinalIgnoreCase))
                                        .ToList();
        if (executableTargets.Count == 1)
        {
            return executableTargets[index: 0];
        }

        throw new InvalidOperationException(
            "Manifest contains multiple candidate targets. " +
            $"Pass --target <name>. Available targets: {string.Join(separator: ", ", values: manifest.Targets.Select(selector: t => t.Name))}");
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
            "  RazorForge cleanbuildandrun [entry-file]        - Clean-rebuild native runtime, then build and execute");
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
            foreach (ISyntaxTreeNode decl in ast.Declarations)
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

            var analyzer = new SemanticVerifier(language: language);
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
        RfBuildMode buildMode = RfBuildMode.Debug, bool saTiming = false)
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

            var target = TargetConfig.ForCurrentHost();
            var analyzer = new SemanticVerifier(language: language,
                target: target, buildMode: buildMode) { SaTiming = saTiming };
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
            var generator = new LlvmCodeGenerator(program: ast,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                target: target,
                buildMode: buildMode,
                synthesizedBodies: result.SynthesizedBodies,
                instantiatedGenericBodies: result.InstantiatedGenericBodies,
                pendingRuntimeDispatches: result.PendingRuntimeDispatches,
                liveRoutineKeys: result.LiveRoutineKeys,
                liveOwnerTypeNames: result.LiveOwnerTypeNames) { Timing = saTiming };
            string llvmIr = generator.Generate();

            // Output
            if (outputFile != null)
            {
                File.WriteAllText(path: outputFile, contents: llvmIr);
                Console.WriteLine(value: $"LLVM IR written to: {outputFile}");
            }
            else
            {
                // Default output file
                string defaultOutput = Path.ChangeExtension(path: sourceFile, extension: ".ll");
                File.WriteAllText(path: defaultOutput, contents: llvmIr);
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
    /// Resolves the directory containing the running RazorForge assembly.
    /// </summary>
    private static string ResolveExecutableDirectory()
    {
        string? exeDir = Path.GetDirectoryName(path: typeof(Program).Assembly.Location);
        return exeDir ?? throw new InvalidOperationException(
            "Unable to resolve the RazorForge executable directory.");
    }

    private static string FindNativeBuildDirectory(string exeDir)
    {
        string? current = exeDir;
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidate = Path.Combine(path1: current, path2: "native", path3: "build");
            if (File.Exists(path: Path.Combine(path1: candidate, path2: "build.ninja")) ||
                File.Exists(path: Path.Combine(path1: candidate, path2: "Makefile")))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(path: current);
        }

        throw new InvalidOperationException(
            "Unable to locate 'native/build' relative to the RazorForge executable.");
    }

    /// <summary>
    /// Rebuilds the native runtime library via cmake --build and copies the fresh artifacts next
    /// to the compiler executable so the linker and final binary observe the same runtime.
    /// Returns 0 on success or 1 if the build fails.
    /// </summary>
    private const string CleanBuildAndRunStage2EnvVar = "RF_CLEAN_BUILDANDRUN_STAGE2";

    /// <summary>
    /// Rebuilds the RazorForge C# project via <c>dotnet build</c>. On Windows the running
    /// apphost holds <c>RazorForge.dll</c> locked against overwrite, so we rename any
    /// existing build outputs aside before invoking msbuild — Windows allows renaming a
    /// loaded DLL even when it cannot be overwritten. After this returns 0, the on-disk
    /// <c>RazorForge.dll</c> / <c>RazorForge.exe</c> reflect the freshly-compiled bits;
    /// the *current* process keeps running the old code, so the caller must re-exec to
    /// pick up the new compiler.
    /// </summary>
    private static int RebuildCompilerProject()
    {
        string? exeDir;
        try
        {
            exeDir = ResolveExecutableDirectory();
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to resolve executable directory: {ex.Message}");
            return 1;
        }

        string? csprojPath = FindRazorForgeCsproj(startDir: exeDir);
        if (csprojPath == null)
        {
            Console.WriteLine(
                value: "Error: could not locate RazorForge.csproj relative to the running executable.");
            return 1;
        }

        // Move the locked .dll/.exe aside so msbuild can write fresh ones. The renamed
        // copies remain mapped into this process; they're cleaned up on a future run.
        foreach (string artifact in new[] { "RazorForge.dll", "RazorForge.exe" })
        {
            string path = Path.Combine(path1: exeDir, path2: artifact);
            if (!File.Exists(path: path)) continue;
            try
            {
                using FileStream probe =
                    File.Open(path: path, mode: FileMode.Open, access: FileAccess.Write,
                        share: FileShare.ReadWrite);
                // Writable — leave it; msbuild will overwrite normally.
            }
            catch (IOException)
            {
                try
                {
                    string sidecar =
                        $"{path}.stale-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
                    File.Move(sourceFileName: path, destFileName: sidecar);
                    TryDeleteSidecars(targetPath: path);
                }
                catch (IOException ex)
                {
                    Console.WriteLine(
                        value: $"Warning: could not move locked '{path}' aside ({ex.Message}).");
                }
            }
        }

        Console.WriteLine(value: "=== REBUILDING RAZORFORGE COMPILER ===");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{csprojPath}\" -c Debug --nologo",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Failed to start dotnet build.");
                return 1;
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.WriteLine(value: $"Compiler rebuild failed (dotnet exited with code {process.ExitCode}).");
                return process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to invoke dotnet build: {ex.Message}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Re-executes the freshly-built compiler with the original args, signalling stage 2 via
    /// an environment variable so the child runs <c>cleanbuildandrun</c> directly without
    /// looping into another compiler rebuild.
    /// </summary>
    private static int ReExecCleanBuildAndRun(string[] args)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath == null || !File.Exists(path: exePath))
        {
            Console.WriteLine(value: "Error: could not determine current executable path for re-exec.");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false
        };
        foreach (string arg in args) psi.ArgumentList.Add(item: arg);
        psi.EnvironmentVariables[CleanBuildAndRunStage2EnvVar] = "1";

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Failed to re-exec freshly-built compiler.");
                return 1;
            }
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to re-exec freshly-built compiler: {ex.Message}");
            return 1;
        }
    }

    private static string? FindRazorForgeCsproj(string startDir)
    {
        string? current = startDir;
        for (int i = 0; i < 8 && current != null; i++)
        {
            string candidate = Path.Combine(path1: current, path2: "RazorForge.csproj");
            if (File.Exists(path: candidate)) return candidate;
            current = Path.GetDirectoryName(path: current);
        }
        return null;
    }

    private static int BuildNativeRuntime(string exeDir, string nativeBuildDir,
        bool cleanFirst = false)
    {
        // Scope a clean rebuild to the razorforge_runtime target only — the cmake project
        // also builds heavy vendored dependencies (sqlite, mbedtls, libsodium, …) that take
        // many minutes to compile from scratch and rarely need rebuilding. `cleanbuildandrun`
        // wants confidence in our own runtime, not a vendored-libs purge.
        string buildArgs = cleanFirst
            ? $"--build \"{nativeBuildDir}\" --target razorforge_runtime --clean-first"
            : $"--build \"{nativeBuildDir}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "cmake",
            Arguments = buildArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start cmake.");
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

            // Also copy DLLs to the exe root (matches csproj LinkBase="." behavior).
            // The compiler itself P/Invokes razorforge_runtime.dll, so the target file may be
            // locked by this process. In that case the already-loaded copy is what this run
            // will use anyway — warn and continue rather than failing the build.
            if (Directory.Exists(path: nativeBinDir))
            {
                foreach (string dll in Directory.GetFiles(path: nativeBinDir,
                             searchPattern: "*.dll"))
                {
                    string dst = Path.Combine(path1: exeDir, path2: Path.GetFileName(path: dll));
                    TryCopyTolerant(src: dll, dst: dst);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to build native runtime: {ex.Message}");
            Console.WriteLine(
                value: "Make sure CMake is installed and the native runtime has been configured.");
            return 1;
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
            string dst = Path.Combine(path1: dstDir, path2: Path.GetFileName(path: file));
            TryCopyTolerant(src: file, dst: dst);
        }
    }

    // Copies a file, tolerating sharing violations when the target is already loaded into this
    // process (e.g. razorforge_runtime.dll, which the compiler itself P/Invokes). On Windows, a
    // loaded DLL is locked against overwrite but can still be renamed — so we move the locked
    // file aside under a unique name and then copy the fresh one into the original path. This
    // guarantees the on-disk artifact is always up to date; the renamed sidecar is harmless and
    // gets cleaned up on a future run when nothing has it open.
    private static void TryCopyTolerant(string src, string dst)
    {
        try
        {
            File.Copy(sourceFileName: src, destFileName: dst, overwrite: true);
            return;
        }
        catch (IOException) when (File.Exists(path: dst))
        {
            // Fall through to rename-aside fallback.
        }

        try
        {
            string staleSidecar =
                $"{dst}.stale-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
            File.Move(sourceFileName: dst, destFileName: staleSidecar);
            File.Copy(sourceFileName: src, destFileName: dst, overwrite: false);
            TryDeleteSidecars(targetPath: dst);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                value:
                $"Warning: could not refresh '{dst}' ({ex.Message}). Using existing copy; rerun to pick up changes.");
        }
    }

    // Best-effort cleanup of previously renamed-aside DLL sidecars. Files still locked by
    // running processes will throw and are ignored.
    private static void TryDeleteSidecars(string targetPath)
    {
        string? dir = Path.GetDirectoryName(path: targetPath);
        if (dir == null) return;
        string prefix = Path.GetFileName(path: targetPath) + ".stale-";
        try
        {
            foreach (string old in Directory.EnumerateFiles(path: dir,
                         searchPattern: prefix + "*"))
            {
                try { File.Delete(path: old); } catch { /* still locked — leave it */ }
            }
        }
        catch { /* directory access issue — non-fatal */ }
    }

    /// <summary>
    /// Returns the full path to the compiler-rt builtins library (e.g. clang_rt.builtins-x86_64.lib)
    /// by asking clang where it lives.
    /// </summary>
    private static string? GetCompilerRtBuiltinsLib()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "clang",
                Arguments = "--print-libgcc-file-name --rtlib=compiler-rt",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && File.Exists(output))
                return output;
        }
        catch
        {
            // clang not available or doesn't support --print-libgcc-file-name
        }
        return null;
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
    /// ??SemanticVerifier.AnalyzeMultiple ??LLVMCodeGenerator with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile,
        string? projectRoot = null, RfBuildMode buildMode = RfBuildMode.Debug,
        bool dumpAst = false, bool saTiming = false, bool requireStartRoutine = true)
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

            var target = TargetConfig.ForCurrentHost();
            var analyzer = new SemanticVerifier(language: language,
                target: target, buildMode: buildMode) { SaTiming = saTiming };
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

            // Executable targets must declare a `start` or `start!` routine in one of the
            // user programs (stdlib doesn't count). Without it codegen would skip @main
            // synthesis and the link step would surface "subsystem must be defined" — make
            // it a hard build error here so the cause is obvious.
            if (requireStartRoutine)
            {
                var userFilePaths = orderedFiles.Select(selector: f => f.FilePath)
                    .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
                bool hasStartRoutine = result.Registry.GetAllRoutines().Any(predicate: r =>
                    r.OwnerType == null &&
                    (r.Name == "start" || r.BaseName.EndsWith(value: ".start")) &&
                    r.Location != null && userFilePaths.Contains(item: r.Location.FileName));
                if (!hasStartRoutine)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        value:
                        "Error: executable target has no 'start' routine. " +
                        "Add 'routine start()' or 'routine start!()' to the entry module, " +
                        "or set the target type to 'library' in razorforge.toml.");
                    return 1;
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
            var generator = new LlvmCodeGenerator(userPrograms: userPrograms,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                target: target,
                buildMode: buildMode,
                synthesizedBodies: result.SynthesizedBodies,
                instantiatedGenericBodies: result.InstantiatedGenericBodies,
                pendingRuntimeDispatches: result.PendingRuntimeDispatches,
                liveRoutineKeys: result.LiveRoutineKeys,
                liveOwnerTypeNames: result.LiveOwnerTypeNames) { Timing = saTiming };
            string llvmIr = generator.Generate();

            // Output
            string outPath = outputFile ?? Path.ChangeExtension(path: entryFile, extension: ".ll");
            File.WriteAllText(path: outPath, contents: llvmIr);
            Console.WriteLine(value: $"LLVM IR written to: {outPath}");

            if (dumpAst)
            {
                string astPath = Path.ChangeExtension(path: outPath, extension: ".rf.desugared");
                var printer = new RfSyntaxTreePrinter();
                string astText = printer.PrintMultiProgram(
                    programs: userPrograms,
                    synthesizedBodies: result.SynthesizedBodies,
                    registry: result.Registry,
                    stdlibPrograms: stdlibPrograms,
                    instantiatedGenericBodies: result.InstantiatedGenericBodies);
                File.WriteAllText(path: astPath, contents: astText);
                Console.WriteLine(value: $"Desugared AST written to: {astPath}");
            }

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

            var analyzer = new SemanticVerifier(language: language);
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
        RfBuildMode buildMode = RfBuildMode.Debug, bool dumpAst = false, bool saTiming = false,
        bool cleanNativeRuntime = false, bool requireStartRoutine = true)
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
            buildMode: buildMode,
            dumpAst: dumpAst,
            saTiming: saTiming,
            requireStartRoutine: requireStartRoutine);
        if (buildResult != 0)
        {
            return buildResult;
        }

        string exeDir;
        string nativeBuildDir;
        try
        {
            exeDir = ResolveExecutableDirectory();
            nativeBuildDir = FindNativeBuildDirectory(exeDir: exeDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to resolve native runtime layout: {ex.Message}");
            return 1;
        }

        int nativeResult = BuildNativeRuntime(exeDir: exeDir,
            nativeBuildDir: nativeBuildDir, cleanFirst: cleanNativeRuntime);
        if (nativeResult != 0)
        {
            return nativeResult;
        }

        string runtimeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");

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
            if (optProcess == null)
            {
                Console.WriteLine(value: "Error: Failed to start opt.");
                return 1;
            }

            string optStderr = optProcess.StandardError.ReadToEnd();
            optProcess.WaitForExit();

            if (optProcess.ExitCode != 0)
            {
                Console.Error.WriteLine(value: optStderr.Trim());
                Console.WriteLine(value: $"Optimization failed (opt exited with code {optProcess.ExitCode})");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute opt: {ex.Message}");
            Console.WriteLine(value: "Make sure LLVM 'opt' is installed and on your PATH.");
            return 1;
        }

        // Compile .ll -> .exe using clang (clang uses -Ox flag style, not opt's -passes= form)
        string clangOptLevel = $"-{optPipelineLevel}";
        // Preserve frame pointers in debug/release for accurate platform stack unwinding.
        // release-time/release-space omit frame pointers for maximum performance.
        string framePointerFlag = buildMode is RfBuildMode.Debug or RfBuildMode.Release
            ? " -fno-omit-frame-pointer"
            : "";
        string windowsThreadingLibs = OperatingSystem.IsWindows()
            ? " -lucrt -lmsvcrt -lkernel32"
            : "";
        // Compiler-RT builtins resolve softfloat/softint symbols that LLVM emits for types
        // without direct hardware support:
        //   fp128 arithmetic: __addtf3, __subtf3, __multf3, __divtf3, __negtf2, __eqtf2, etc.
        //   f16 conversions:  __extendhfsf2, __truncsfhf2
        //   i128 arithmetic:  __divti3, __modti3, __udivti3, __umodti3
        //
        // On Windows, neither MSVC link.exe nor lld-link automatically searches for the clang
        // compiler-rt builtins library when linking an .ll/.obj that was generated from LLVM IR
        // (rather than from a C/C++ source file). We locate the library explicitly via
        //   clang --print-libgcc-file-name --rtlib=compiler-rt
        // and add it directly to the linker command line.
        string compilerRtArg;
        if (OperatingSystem.IsWindows())
        {
            string? compilerRtLib = GetCompilerRtBuiltinsLib();
            if (string.IsNullOrWhiteSpace(value: compilerRtLib))
            {
                Console.WriteLine(
                    value: "Failed to locate clang compiler-rt builtins library on Windows.");
                return 1;
            }

            compilerRtArg = $" \"{compilerRtLib}\"";
        }
        else
        {
            compilerRtArg = " --rtlib=compiler-rt";
        }
        string lldFlag = OperatingSystem.IsWindows() ? " -fuse-ld=lld" : "";
        // Surface every undefined-symbol error during development instead of stopping at lld-link's
        // default cap (~20). lld-link uses /errorlimit:N; ld.lld has no equivalent cap.
        string linkerErrorLimitFlag =
            OperatingSystem.IsWindows() ? " -Wl,/errorlimit:0" : "";
        string clangArgs =
            $"{clangOptLevel}{framePointerFlag}{lldFlag} -o \"{exeFile}\" \"{optFile}\" -L\"{runtimeLibDir}\" -lrazorforge_runtime{compilerRtArg}{windowsThreadingLibs}{linkerErrorLimitFlag}";

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

            // Read stdout/stderr concurrently to avoid pipe-buffer deadlock when clang/lld
            // emits a lot of output (e.g. many LNK2019 errors on a ~60k-line IR).
            Task<string> stdoutTask = clangProcess.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = clangProcess.StandardError.ReadToEndAsync();
            clangProcess.WaitForExit();
            string clangStdout = stdoutTask.GetAwaiter().GetResult();
            string clangStderr = stderrTask.GetAwaiter().GetResult();

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
                TryCopyTolerant(src: srcDll, dst: dstDll);
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
