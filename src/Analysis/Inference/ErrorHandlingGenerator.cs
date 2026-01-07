namespace Compilers.Analysis.Inference;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;
using TypeInfo = Compilers.Analysis.Types.TypeInfo;

/// <summary>
/// Generates error handling variants for failable (!) routines.
///
/// Generation rules based on throw/absent usage:
/// - Only absent: try_ (returns T?)
/// - Only throw: try_ (returns T?) + check_ (returns Result&lt;T&gt;)
/// - Both: try_ (returns T?) + lookup_ (returns Lookup&lt;T&gt;)
///
/// Phase 1: Keyword Detection - scan for throw/absent in body
/// Phase 2: Variant Generation - determine which variants to create
/// Phase 3: Code Transformation - generate variant routines
/// </summary>
public sealed class ErrorHandlingGenerator
{
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorHandlingGenerator"/> class.
    /// </summary>
    /// <param name="registry">The type registry for lookups and registration.</param>
    public ErrorHandlingGenerator(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Generates a variant name from an original routine name.
    /// For qualified names like "Cache.get", produces "Cache.try_get".
    /// For simple names like "get", produces "try_get".
    /// </summary>
    /// <param name="prefix">The variant prefix (try, check, find).</param>
    /// <param name="originalName">The original routine name.</param>
    /// <returns>The variant name.</returns>
    private static string GenerateVariantName(string prefix, string originalName)
    {
        int lastDot = originalName.LastIndexOf(value: '.');
        if (lastDot >= 0)
        {
            // Qualified name: "Type.method" -> "Type.prefix_method"
            return $"{originalName[..(lastDot + 1)]}{prefix}_{originalName[(lastDot + 1)..]}";
        }

        // Simple name: "method" -> "prefix_method"
        return $"{prefix}_{originalName}";
    }

    /// <summary>
    /// Analyzes a failable routine and generates appropriate variants.
    /// </summary>
    /// <param name="routine">The routine to analyze.</param>
    /// <param name="body">The routine's body statement.</param>
    /// <returns>The result containing generated variants and any errors.</returns>
    public ErrorHandlingResult GenerateVariants(RoutineInfo routine, Statement body)
    {
        if (!routine.IsFailable)
        {
            return ErrorHandlingResult.Empty;
        }

        // Phase 1: Keyword Detection
        ErrorHandlingAnalysis analysis = AnalyzeBody(body: body);

        // Validate: ! functions must use throw or absent
        if (analysis is { HasThrow: false, HasAbsent: false })
        {
            return new ErrorHandlingResult
            {
                Error = $"Failable function '{routine.Name}!' must use 'throw' or 'absent'",
                HasThrow = false,
                HasAbsent = false
            };
        }

        // Phase 2: Variant Generation
        var variants = new List<GeneratedVariant>();

        // try_ variant is always generated
        RoutineInfo tryVariant = GenerateTryVariant(original: routine);
        variants.Add(item: new GeneratedVariant(
            Kind: ErrorHandlingVariantKind.Try,
            Routine: tryVariant));

        // check_ variant if only throw (no absent)
        if (analysis is { HasThrow: true, HasAbsent: false })
        {
            RoutineInfo checkVariant = GenerateCheckVariant(original: routine);
            variants.Add(item: new GeneratedVariant(
                Kind: ErrorHandlingVariantKind.Check,
                Routine: checkVariant));
        }

        // lookup_ variant if both throw and absent
        if (analysis is { HasThrow: true, HasAbsent: true })
        {
            RoutineInfo findVariant = GenerateFindVariant(original: routine);
            variants.Add(item: new GeneratedVariant(
                Kind: ErrorHandlingVariantKind.Find,
                Routine: findVariant));
        }

        return new ErrorHandlingResult
        {
            Variants = variants,
            HasThrow = analysis.HasThrow,
            HasAbsent = analysis.HasAbsent
        };
    }

    /// <summary>
    /// Phase 1: Analyzes the body for throw/absent keywords.
    /// </summary>
    /// <param name="body">The statement body to analyze.</param>
    /// <returns>Analysis result with throw/absent flags.</returns>
    private ErrorHandlingAnalysis AnalyzeBody(Statement body)
    {
        var analysis = new ErrorHandlingAnalysis();
        AnalyzeStatementRecursive(statement: body, analysis: analysis);
        return analysis;
    }

    /// <summary>
    /// Recursively analyzes statements for throw/absent keywords.
    /// </summary>
    /// <param name="statement">The statement to analyze.</param>
    /// <param name="analysis">The analysis result to update.</param>
    private void AnalyzeStatementRecursive(Statement statement, ErrorHandlingAnalysis analysis)
    {
        // TODO: Is this all the branch?
        switch (statement)
        {
            case ThrowStatement:
                analysis.HasThrow = true;
                break;

            case AbsentStatement:
                analysis.HasAbsent = true;
                break;

            case BlockStatement block:
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatementRecursive(statement: stmt, analysis: analysis);
                }

                break;

            case IfStatement ifStmt:
                AnalyzeStatementRecursive(statement: ifStmt.ThenStatement, analysis: analysis);
                if (ifStmt.ElseStatement != null)
                {
                    AnalyzeStatementRecursive(statement: ifStmt.ElseStatement, analysis: analysis);
                }

                break;

            case WhileStatement whileStmt:
                AnalyzeStatementRecursive(statement: whileStmt.Body, analysis: analysis);
                break;

            case ForStatement forStmt:
                AnalyzeStatementRecursive(statement: forStmt.Body, analysis: analysis);
                break;

            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    AnalyzeStatementRecursive(statement: clause.Body, analysis: analysis);
                }

                break;
        }
    }

    /// <summary>
    /// Generates the try_ variant (returns Maybe&lt;T&gt;).
    /// throw → return None
    /// absent → return None
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The try_ variant routine info.</returns>
    private RoutineInfo GenerateTryVariant(RoutineInfo original)
    {
        TypeInfo returnType = original.ReturnType ?? throw new InvalidOperationException(
            message: "Failable function must have a return type");

        TypeInfo maybeType = _registry.GetOrCreateInstantiation(
            genericDef: ErrorHandlingTypeInfo.WellKnown.MaybeDefinition,
            typeArguments: [returnType]);

        return new RoutineInfo(name: GenerateVariantName(prefix: "try", originalName: original.Name))
        {
            Kind = original.Kind,
            OwnerType = original.OwnerType,
            Parameters = original.Parameters,
            ReturnType = maybeType,
            IsFailable = false, // try_ variants don't fail
            DeclaredMutation = original.DeclaredMutation,
            MutationCategory = original.MutationCategory,
            GenericParameters = original.GenericParameters,
            GenericConstraints = original.GenericConstraints,
            Visibility = original.Visibility,
            Location = original.Location,
            Namespace = original.Namespace,
            Attributes = original.Attributes
        };
    }

    /// <summary>
    /// Generates the check_ variant (returns Result&lt;T&gt;).
    /// throw → return error
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The check_ variant routine info.</returns>
    private RoutineInfo GenerateCheckVariant(RoutineInfo original)
    {
        TypeInfo returnType = original.ReturnType ?? throw new InvalidOperationException(
            message: "Failable function must have a return type");

        TypeInfo resultType = _registry.GetOrCreateInstantiation(
            genericDef: ErrorHandlingTypeInfo.WellKnown.ResultDefinition,
            typeArguments: [returnType]);

        return new RoutineInfo(name: GenerateVariantName(prefix: "check", originalName: original.Name))
        {
            Kind = original.Kind,
            OwnerType = original.OwnerType,
            Parameters = original.Parameters,
            ReturnType = resultType,
            IsFailable = false, // check_ variants don't fail
            DeclaredMutation = original.DeclaredMutation,
            MutationCategory = original.MutationCategory,
            GenericParameters = original.GenericParameters,
            GenericConstraints = original.GenericConstraints,
            Visibility = original.Visibility,
            Location = original.Location,
            Namespace = original.Namespace,
            Attributes = original.Attributes
        };
    }

    /// <summary>
    /// Generates the lookup_ variant (returns Lookup&lt;T&gt;).
    /// throw → return error
    /// absent → return None
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The lookup_ variant routine info.</returns>
    private RoutineInfo GenerateFindVariant(RoutineInfo original)
    {
        TypeInfo returnType = original.ReturnType ?? throw new InvalidOperationException(
            message: "Failable function must have a return type");

        TypeInfo lookupType = _registry.GetOrCreateInstantiation(
            genericDef: ErrorHandlingTypeInfo.WellKnown.LookupDefinition,
            typeArguments: [returnType]);

        return new RoutineInfo(name: GenerateVariantName(prefix: "find", originalName: original.Name))
        {
            Kind = original.Kind,
            OwnerType = original.OwnerType,
            Parameters = original.Parameters,
            ReturnType = lookupType,
            IsFailable = false, // lookup_ variants don't fail
            DeclaredMutation = original.DeclaredMutation,
            MutationCategory = original.MutationCategory,
            GenericParameters = original.GenericParameters,
            GenericConstraints = original.GenericConstraints,
            Visibility = original.Visibility,
            Location = original.Location,
            Namespace = original.Namespace,
            Attributes = original.Attributes
        };
    }
}
