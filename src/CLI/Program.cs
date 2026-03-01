using Compiler.Diagnostics;
using Compiler.Lexer;
using Compiler.Parser;
using SyntaxTree;
using SemanticAnalysis;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Results;
using Compiler.CodeGen;

namespace Builder;

/// <summary>
/// Minimal CLI for testing the lexer and parser during the overhaul.
/// Analysis and CodeGen will be added back once the new type system is implemented.
/// </summary>
internal class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant().TrimStart('-');

        // Check if first arg is a command or a file
        bool isCommand = command == "parse" || command == "tokenize" || command == "codegen" || command == "emit" || command == "validate-stdlib" || command == "help";

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

    private static void PrintUsage()
    {
        Console.WriteLine("RazorForge Builder");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  RazorForge <source-file>                    - Parse file and show AST summary");
        Console.WriteLine("  RazorForge parse <source-file>              - Parse file and show AST summary");
        Console.WriteLine("  RazorForge tokenize <source-file>           - Tokenize file and show tokens");
        Console.WriteLine("  RazorForge codegen <source-file> [out.ll]   - Generate LLVM IR");
        Console.WriteLine("  RazorForge validate-stdlib [rf|sf]           - Validate stdlib routine bodies");
        Console.WriteLine("  RazorForge help                             - Show this help");
        Console.WriteLine();
        Console.WriteLine("  <source-file>: .rf file for RazorForge or .sf file for Suflae");
    }

    private static bool IsSuflaeFile(string path) => path.EndsWith(".sf", StringComparison.OrdinalIgnoreCase);

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

    private static List<string> GetModifiers(RoutineDeclaration func)
    {
        var mods = new List<string>();
        if (func.GenericParameters?.Count > 0) mods.Add($"[{string.Join(", ", func.GenericParameters)}]");
        return mods;
    }

    private static List<string> GetModifiers(RecordDeclaration rec)
    {
        var mods = new List<string>();
        if (rec.GenericParameters?.Count > 0) mods.Add($"[{string.Join(", ", rec.GenericParameters)}]");
        return mods;
    }

    private static List<string> GetModifiers(EntityDeclaration ent)
    {
        var mods = new List<string>();
        if (ent.GenericParameters?.Count > 0) mods.Add($"[{string.Join(", ", ent.GenericParameters)}]");
        return mods;
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
