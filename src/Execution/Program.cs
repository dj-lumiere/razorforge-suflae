using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Compiler.CodeGen;
using Compiler.Declaration;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
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
    private const string BuildCommand = "build";
    private const string SuflaeLanguageName = "Suflae";
    private const string RazorForgeLanguageName = "RazorForge";

    /// <summary>
    /// Entry point for the RazorForge builder CLI.
    /// Dispatches to the appropriate command handler based on the first argument.
    /// Returns 0 on success or 1 on error.
    /// </summary>
    public static int Main(string[] args)
    {
        RuntimeShadowLoader.Install();

        // Make the build driver byte-faithful for UTF-8. RF child processes write UTF-8 and
        // we forward their stdout/stderr to ours; if Console encodings default to the
        // system ACP (Korean CP949, Western CP1252, ...), every non-ASCII byte gets
        // rewritten as `?` somewhere in the read/write chain. Forcing UTF-8 on both input
        // and output makes the pipe a passthrough.
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0]
                        .ToLowerInvariant()
                        .TrimStart(trimChar: '-');

        // Check if first arg is a command or a file
        bool isCommand = command is "parse" or "tokenize" or "codegen" or BuildCommand or "buildandrun" or "check" or "validate-stdlib" or "help" or "version" or "v";

        if (command is "version" or "v")
        {
            PrintVersion();
            return 0;
        }

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

            case BuildCommand:
            {
                // `build` compiles all the way to a native executable for the HOST OS
                // (codegen -> opt -> link -> stage runtime DLLs) but does NOT run it. The
                // intermediate <entry>.ll / .opt.ll are kept as byproducts for inspection;
                // `codegen` remains the IR-only verb. (All-OS artifacts come from the release CI.)
                (string? entryFile, string? projectRoot, _,
                    RfBuildMode buildMode2, bool dumpAst2, bool saTiming2, bool requireStart2,
                    bool showStages2, IReadOnlyList<string> libraryRoots2) = ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                int buildRc = BuildExecutable(entryFile: entryFile,
                    exeFile: out string builtExe,
                    projectRoot: projectRoot,
                    buildMode: buildMode2,
                    dumpAst: dumpAst2,
                    saTiming: saTiming2,
                    requireStartRoutine: requireStart2,
                    showBuildStages: showStages2,
                    libraryRoots: libraryRoots2);
                if (buildRc == 0)
                {
                    Console.WriteLine(value: $"Executable written to: {Path.GetFullPath(path: builtExe)}");
                }

                return buildRc;
            }

            case "buildandrun":
            {
                (string? entryFile, string? projectRoot, _,
                    RfBuildMode buildMode3, bool dumpAst3, bool saTiming3, bool requireStart3,
                    bool showStages3, IReadOnlyList<string> libraryRoots3) = ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                return BuildAndRun(entryFile: entryFile,
                    projectRoot: projectRoot,
                    buildMode: buildMode3,
                    dumpAst: dumpAst3,
                    saTiming: saTiming3,
                    requireStartRoutine: requireStart3,
                    showBuildStages: showStages3,
                    libraryRoots: libraryRoots3);
            }

            case "check":
            {
                (string? entryFile, string? projectRoot, _, _, _, _, _, _,
                    IReadOnlyList<string> libraryRoots4) =
                    ResolveEntryFile(args: args, needsOutputArg: false);
                if (entryFile == null)
                {
                    return 1;
                }

                return CheckMultiFile(entryFile: entryFile, projectRoot: projectRoot,
                    libraryRoots: libraryRoots4);
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
    /// Searches for a razorforge.toml manifest in all cases: when no entry file is given the
    /// manifest supplies the executable; when an explicit entry file is given it overrides
    /// [target] executable but the manifest's other settings still apply.
    /// ALL build configuration lives in the manifest's [target] section (executable, library,
    /// mode, dump-ast, sa-timing, show-build-stages) — the CLI deliberately takes no flags.
    /// Returns (entryFile, projectRoot, outputFile, buildMode, dumpAst, saTiming, requireStartRoutine, showBuildStages); entryFile is null on error.
    /// </summary>
    private static (string? EntryFile, string? ProjectRoot, string? OutputFile,
        RfBuildMode BuildMode, bool DumpAst, bool SaTiming, bool RequireStartRoutine, bool ShowBuildStages,
        IReadOnlyList<string> LibraryRoots) ResolveEntryFile(string[] args, bool needsOutputArg) // NOSONAR S3776
    {
        // args[0] is the command name (build/buildandrun/check)
        string? explicitEntry = null;
        string? outputFile = null;

        // Parse remaining args (positional only: [entry-file] [out.ll])
        int i = 1;
        while (i < args.Length)
        {
            if (!args[i]
                   .StartsWith('-'))
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
                Console.WriteLine(
                    value:
                    $"Error: unknown option '{args[i]}'. RazorForge takes no build flags — configure builds in razorforge.toml ([target] executable, library, mode, ...).");
                return (null, null, null, RfBuildMode.Debug, false, false, false, false, []);
            }
        }

        // Explicit source file given — use it as the entry point, but still honor the
        // nearest razorforge.toml (walking up from the file's directory): the manifest
        // remains the single source of build configuration (mode, library deps, debug
        // fields) even for single-file builds; only [target] executable is overridden.
        // .toml files are treated as manifests, not source files.
        if (explicitEntry != null &&
            !explicitEntry.EndsWith(value: ".toml", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path: explicitEntry))
            {
                Console.WriteLine(value: $"Error: File '{explicitEntry}' not found.");
                return (null, null, null, RfBuildMode.Debug, false, false, false, false, []);
            }

            string entryDir =
                Path.GetDirectoryName(path: Path.GetFullPath(path: explicitEntry)) ?? ".";
            string? nearbyManifest = ManifestLoader.FindManifest(startDir: entryDir);
            if (nearbyManifest == null)
            {
                // Truly manifest-less — debug defaults. Assume an executable build so
                // codegen knows to synthesize @main and SA can require a 'start' routine.
                return (explicitEntry, entryDir, outputFile, RfBuildMode.Debug, false, false,
                    true, false, []);
            }

            try
            {
                ProjectManifest manifest = ManifestLoader.Load(tomlPath: nearbyManifest,
                    resolveExecutable: false);
                BuildTarget target = manifest.Target;
                RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

                if (target.ShowBuildStages)
                {
                    Console.WriteLine(value: $"Using manifest: {nearbyManifest}");
                    Console.WriteLine(
                        value:
                        $"Executable: {explicitEntry} ({target.Mode}, entry from command line)");
                    if (target.Libraries.Count > 0)
                    {
                        Console.WriteLine(
                            value:
                            $"Libraries: {string.Join(separator: ", ", values: target.Libraries)}");
                    }
                }

                return (explicitEntry, manifest.ManifestDirectory, outputFile, buildMode,
                    target.DumpAst, target.SaTiming, true, target.ShowBuildStages,
                    target.Libraries);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
                return (null, null, null, RfBuildMode.Debug, false, false, false, false, []);
            }
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

            return (null, null, null, RfBuildMode.Debug, false, false, false, false, []);
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath);
            BuildTarget target = manifest.Target;

            RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

            bool showBuildStages = target.ShowBuildStages;
            if (showBuildStages)
            {
                Console.WriteLine(value: $"Using manifest: {manifestPath}");
                Console.WriteLine(value: $"Executable: {target.Executable} ({target.Mode})");
                if (target.Libraries.Count > 0)
                {
                    Console.WriteLine(
                        value:
                        $"Libraries: {string.Join(separator: ", ", values: target.Libraries)}");
                }
            }

            return (target.Executable, manifest.ManifestDirectory, outputFile, buildMode,
                target.DumpAst, target.SaTiming, true, showBuildStages, target.Libraries);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return (null, null, null, RfBuildMode.Debug, false, false, false, false, []);
        }
    }

    /// <summary>
    /// Maps a [target] mode string to its <see cref="RfBuildMode"/>; throws on unknown modes.
    /// </summary>
    private static RfBuildMode ParseBuildMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "debug" => RfBuildMode.Debug,
            "release" => RfBuildMode.Release,
            "release-time" => RfBuildMode.ReleaseTime,
            "release-space" => RfBuildMode.ReleaseSpace,
            _ => throw new InvalidOperationException(
                $"Unknown build mode '{mode}' in [target]. " +
                "Valid modes are: debug, release, release-time, release-space.")
        };
    }

    /// <summary>
    /// Prints the CLI usage instructions to standard output.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine(value: $"RazorForge Builder {GetVersionString()}");
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
            value: "  RazorForge build [entry-file]                   - Build a native executable (host OS, no run)");
        Console.WriteLine(
            value: "  RazorForge buildandrun [entry-file]             - Build and execute");
        Console.WriteLine(
            value:
            "  RazorForge check [entry-file]                   - Type-check only (no codegen)");
        Console.WriteLine(
            value:
            "  RazorForge validate-stdlib [rf|sf]              - Validate stdlib routine bodies");
        Console.WriteLine(
            value: "  RazorForge help                                 - Show this help");
        Console.WriteLine(
            value: "  RazorForge version                              - Show compiler version");
        Console.WriteLine();
        Console.WriteLine(
            value: "  <source-file>: .rf file for RazorForge or .sf file for Suflae");
        Console.WriteLine(
            value: "  If no entry file is given, searches for razorforge.toml in the current");
        Console.WriteLine(value: "  directory and parent directories.");
        Console.WriteLine();
        Console.WriteLine(
            value: "  There are no build flags: all build configuration lives in razorforge.toml's");
        Console.WriteLine(
            value: "  [target] section (executable, library, mode, show-build-stages, ...).");
    }

    /// <summary>Prints the compiler version (from assembly metadata) to standard output.</summary>
    private static void PrintVersion()
    {
        Console.WriteLine(value: $"RazorForge {GetVersionString()}");
    }

    /// <summary>
    /// Returns the compiler version string from assembly metadata, preferring the
    /// informational version (e.g. "0.0.1-alpha") and stripping any "+commit" suffix.
    /// </summary>
    private static string GetVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        string version = assembly
                        .GetCustomAttributes(
                             attributeType: typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                             inherit: false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault()
                       ?.InformationalVersion
                     ?? assembly.GetName()
                                .Version
                               ?.ToString()
                     ?? "unknown";
        int plusIndex = version.IndexOf(value: '+');
        return plusIndex > 0 ? $"v{version[..plusIndex]}" : $"v{version}";
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
            value: $"Tokenizing {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
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
            value: $"Parsing {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
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
            List<BuildWarning> warnings = parser.GetWarnings();

            Console.WriteLine(
                value: $"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Show warnings if any
            if (warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({warnings.Count}) ===");
                foreach (BuildWarning warning in warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
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
            DiagnosticRenderer.Print(ex: ex);
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
                ? SuflaeLanguageName
                : RazorForgeLanguageName;
            Console.WriteLine(value: $"Validating {langName} stdlib routine bodies...");
            Console.WriteLine();

            var analyzer = new SemanticVerifier(language: language);
            List<SemanticError> stdlibErrors = analyzer.ValidateStdlibBodies();

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
                    DiagnosticRenderer.Print(error: error, indent: "    ");
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
    /// Runs the full compiler pipeline (tokenize -> parse -> semantic analysis -> LLVM IR generation)
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
            value: $"Building {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
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
                Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: result.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                DiagnosticRenderer.PrintAll(warnings: result.Warnings);
            }

            // Code Generation
            Console.WriteLine();
            Console.WriteLine(value: "=== CODE GENERATION ===");

            // Pass stdlib programs to codegen so intrinsic routines get built
            List<(SyntaxTree.Program Program, string FilePath, string Module)>
                stdlibPrograms = result.Registry.StdlibPrograms;

            // 5b-2: instrument may-suspend routine bodies with cancellation push/pop markers
            // (no-op unless something reaches a coroutine suspend point). Mutates `ast` in place,
            // which is the same AST object codegen consumes below.
            Compiler.Postprocessing.Passes.CancellationInstrumentationPass.Run(
                programs: [(ast, ast.Location.FileName, "")],
                maySuspendKeys: result.MaySuspendRoutineKeys,
                registry: result.Registry);

            var generator = new LlvmCodeGenerator(program: ast,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                target: target,
                buildMode: buildMode,
                synthesizedBodies: result.SynthesizedBodies,
                instantiatedGenericBodies: result.InstantiatedGenericBodies,
                liveRoutineKeys: result.LiveRoutineKeys,
                liveOwnerTypeNames: result.LiveOwnerTypeNames,
                maySuspendRoutineKeys: result.MaySuspendRoutineKeys) { Timing = saTiming };
            string llvmIr = generator.Generate();
            Console.WriteLine(value: $"Routines emitted: {generator.EmittedRoutineCount}");

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
            DiagnosticRenderer.Print(ex: ex);
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
    /// Runs the multi-file build pipeline: BuildDriver (parse + resolve imports + topo sort)
    /// -> SemanticVerifier.AnalyzeMultiple -> LLVMCodeGenerator with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile,
        string? projectRoot = null, RfBuildMode buildMode = RfBuildMode.Debug,
        bool dumpAst = false, bool saTiming = false, bool requireStartRoutine = true,
        bool showBuildStages = false, IReadOnlyList<string>? libraryRoots = null)
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

        if (showBuildStages)
        {
            Console.WriteLine(
                value:
                $"Building {entryFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)} (multi-file)...");
            Console.WriteLine();
        }

        try
        {
            // Use provided project root (from manifest) or fall back to entry file directory
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            if (showBuildStages)
                Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language,
                libraryRoots: libraryRoots);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            if (showBuildStages)
                Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: buildResult.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Build aborted due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.Error.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
                foreach (BuildWarning warning in buildResult.Warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
                }
            }

            if (showBuildStages)
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
            if (showBuildStages)
            {
                Console.WriteLine();
                Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");
            }

            var target = TargetConfig.ForCurrentHost();
            var analyzer = new SemanticVerifier(language: language,
                target: target, buildMode: buildMode) { SaTiming = saTiming };
            // Share the driver's fully-indexed resolver so SA-phase imports see the same
            // module set the build graph resolved (incl. [target] library directories).
            analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);

            if (showBuildStages)
                Console.WriteLine(
                    value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: result.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                DiagnosticRenderer.PrintAll(warnings: result.Warnings);
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
            if (showBuildStages)
            {
                Console.WriteLine();
                Console.WriteLine(value: "=== CODE GENERATION ===");
            }

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

            List<(SyntaxTree.Program Program, string FilePath, string Module)>
                stdlibPrograms = result.Registry.StdlibPrograms;

            // 5b-2: instrument may-suspend routine bodies with cancellation push/pop markers
            // (no-op unless something reaches a coroutine suspend point). Mutates the userPrograms
            // ASTs in place — the same objects codegen consumes below.
            Compiler.Postprocessing.Passes.CancellationInstrumentationPass.Run(
                programs: userPrograms,
                maySuspendKeys: result.MaySuspendRoutineKeys,
                registry: result.Registry);

            var generator = new LlvmCodeGenerator(userPrograms: userPrograms,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                target: target,
                buildMode: buildMode,
                synthesizedBodies: result.SynthesizedBodies,
                instantiatedGenericBodies: result.InstantiatedGenericBodies,
                liveRoutineKeys: result.LiveRoutineKeys,
                liveOwnerTypeNames: result.LiveOwnerTypeNames,
                maySuspendRoutineKeys: result.MaySuspendRoutineKeys) { Timing = saTiming };
            string llvmIr = generator.Generate();
            if (showBuildStages)
                Console.Error.WriteLine(value: $"Routines emitted: {generator.EmittedRoutineCount}");

            // Output
            string outPath = outputFile ?? Path.ChangeExtension(path: entryFile, extension: ".ll");
            File.WriteAllText(path: outPath, contents: llvmIr);
            if (showBuildStages)
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
                if (showBuildStages)
                    Console.WriteLine(value: $"Desugared AST written to: {astPath}");
            }

            if (showBuildStages)
            {
                Console.WriteLine();
                Console.WriteLine(value: "Build successful!");
            }
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
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
    private static int CheckMultiFile(string entryFile, string? projectRoot = null,
        IReadOnlyList<string>? libraryRoots = null) // NOSONAR S3776
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
            $"Checking {entryFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)} (multi-file)...");
        Console.WriteLine();

        try
        {
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language,
                libraryRoots: libraryRoots);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: buildResult.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.Error.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
                foreach (BuildWarning warning in buildResult.Warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
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

            // Phase 2: Semantic analysis (multi-file) -> no codegen
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticVerifier(language: language);
            // Share the driver's fully-indexed resolver so SA-phase imports see the same
            // module set the build graph resolved (incl. [target] library directories).
            analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: result.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                DiagnosticRenderer.PrintAll(warnings: result.Warnings);
            }

            Console.WriteLine();
            Console.WriteLine(value: "Check passed!");
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
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
    /// <summary>
    /// Compiles <paramref name="entryFile"/> all the way to a native executable
    /// (codegen → opt → link → stage runtime DLLs) but does NOT run it. On success returns 0 and
    /// sets <paramref name="exeFile"/> to the produced executable path. Shared by the <c>buildexe</c>
    /// verb (stop here) and <c>buildandrun</c> (run it next) so the slow optimize+link is identical.
    /// </summary>
    private static int BuildExecutable(string entryFile, out string exeFile, string? projectRoot = null,
        RfBuildMode buildMode = RfBuildMode.Debug, bool dumpAst = false, bool saTiming = false,
        bool requireStartRoutine = true, bool showBuildStages = false,
        IReadOnlyList<string>? libraryRoots = null)
    {
        // Remove stale per-target outputs before rebuilding.
        string llFile = Path.ChangeExtension(path: entryFile, extension: ".ll");
        string optFile = Path.ChangeExtension(path: llFile, extension: ".opt.ll");
        exeFile = Path.ChangeExtension(path: llFile, extension: ".exe");
        NativeToolchain.CleanBuildAndRunOutputs(llFile: llFile, optFile: optFile, exeFile: exeFile);

        // Build first (to a temp .ll file)
        int buildResult = BuildMultiFile(entryFile: entryFile,
            outputFile: llFile,
            projectRoot: projectRoot,
            buildMode: buildMode,
            dumpAst: dumpAst,
            saTiming: saTiming,
            requireStartRoutine: requireStartRoutine,
            showBuildStages: showBuildStages,
            libraryRoots: libraryRoots);
        if (buildResult != 0)
        {
            return buildResult;
        }

        string exeDir;
        string runtimeLibDir;
        try
        {
            exeDir = NativeToolchain.ResolveExecutableDirectory();
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to resolve native runtime layout: {ex.Message}");
            return 1;
        }

        if (NativeToolchain.TryFindNativeBuildDirectory(exeDir: exeDir, nativeBuildDir: out string nativeBuildDir))
        {
            // Development checkout: rebuild the native runtime incrementally before linking.
            int nativeResult = NativeToolchain.BuildNativeRuntime(exeDir: exeDir,
                nativeBuildDir: nativeBuildDir);
            if (nativeResult != 0)
            {
                return nativeResult;
            }

            runtimeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");
        }
        else if (File.Exists(path: Path.Combine(path1: exeDir, path2: NativeToolchain.RuntimeLinkLibraryFileName)))
        {
            // Installed/published layout: prebuilt runtime artifacts ship flat next to the
            // executable (csproj LinkBase="." / the packaging scripts) — nothing to rebuild.
            runtimeLibDir = exeDir;
        }
        else
        {
            Console.WriteLine(
                value:
                $"Failed to resolve the RazorForge native runtime: expected either a development 'native/build' tree near the executable, or '{NativeToolchain.RuntimeLinkLibraryFileName}' next to it (installed layout).");
            return 1;
        }

        // Optimize the emitted IR, then link it into a native executable.
        int optResult = NativeToolchain.OptimizeIr(llFile: llFile, optFile: optFile,
            buildMode: buildMode);
        if (optResult != 0)
        {
            return optResult;
        }

        int linkResult = NativeToolchain.LinkExecutable(optFile: optFile, exeFile: exeFile,
            runtimeLibDir: runtimeLibDir, buildMode: buildMode);
        if (linkResult != 0)
        {
            return linkResult;
        }

        // Copy the runtime DLL (and its shared-library dependencies) next to the
        // output .exe so the loader can find them at runtime.
        NativeToolchain.StageRuntimeDlls(exeDir: exeDir, exeFile: exeFile);
        return 0;
    }

    private static int BuildAndRun(string entryFile, string? projectRoot = null,
        RfBuildMode buildMode = RfBuildMode.Debug, bool dumpAst = false, bool saTiming = false,
        bool requireStartRoutine = true,
        bool showBuildStages = false, IReadOnlyList<string>? libraryRoots = null)
    {
        int buildResult = BuildExecutable(entryFile: entryFile,
            exeFile: out string exeFile,
            projectRoot: projectRoot,
            buildMode: buildMode,
            dumpAst: dumpAst,
            saTiming: saTiming,
            requireStartRoutine: requireStartRoutine,
            showBuildStages: showBuildStages,
            libraryRoots: libraryRoots);
        if (buildResult != 0)
        {
            return buildResult;
        }

        // Run the produced .exe
        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== EXECUTION ===");
        }

        bool stdinIsPiped = Console.IsInputRedirected;
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(path: exeFile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinIsPiped,
            // RF programs write UTF-8 (rf_runtime_init sets SetConsoleOutputCP(65001) on
            // Windows and stdlib paths encode every Text via UTF-8). Without these explicit
            // encodings, .NET's StreamReader defaults to the parent's Console.OutputEncoding
            // (system ACP — CP949 / CP1252 / etc. depending on locale) and rewrites every
            // non-ACP byte as `?`, garbling all non-ASCII output (Korean, emoji, accented
            // Latin, …). Setting these to UTF-8 makes the readers byte-faithful.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Error: Failed to start the compiled executable.");
                return 1;
            }

            // Forward our stdin to the child CONCURRENTLY — never synchronously before draining
            // the child's output. When this process's own stdin is a redirected pipe that never
            // reaches EOF (the common in-harness / CI case), a synchronous CopyTo blocks forever:
            // it waits for our stdin to end while the child fills its stdout pipe with nobody
            // draining it, so both sides wedge (the long-standing "buildandrun stalls in harness"
            // bug). On a background task the copy can't stall the output drain; it ends when our
            // stdin closes or the child's stdin pipe does. It's a background thread, so a copy that
            // never completes (parent stdin held open) does not keep the process alive.
            if (stdinIsPiped)
            {
                _ = Task.Run(action: () =>
                {
                    try
                    {
                        Console.OpenStandardInput()
                               .CopyTo(destination: process.StandardInput.BaseStream);
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        // Child exited / its stdin pipe closed — nothing left to forward.
                    }
                });
            }

            // Drain stdout and stderr CONCURRENTLY. Reading them sequentially (all of stdout,
            // then all of stderr) deadlocks whenever the child fills the OS stderr pipe buffer
            // while we are still blocked on stdout: the child blocks writing stderr, we block
            // reading stdout, and neither side progresses. Kicking off both async reads first
            // keeps both pipes draining continuously.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
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

}
