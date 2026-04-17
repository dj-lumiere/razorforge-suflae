using Compiler.Synthesis;
using SemanticVerification.Symbols;
using SyntaxTree;

namespace Compiler.Desugaring.Passes;


/// <summary>
/// Generates try_/check_/lookup_ routine variants for all failable routines.
/// Runs once globally after Phase 5 body analysis.
///
/// Generation rules (based on throw/absent found in body):
/// - Only absent:       try_
/// - Only throw:        try_ + check_
/// - Both:              try_ + lookup_
/// </summary>
internal sealed class ErrorHandlingVariantPass(DesugaringContext ctx)
{
    /// <summary>Per-file stub — variant generation is global only.</summary>
    public void Run(Program program) { }

    /// <summary>
    /// Runs variant generation globally.
    /// Must be called once after all routine bodies have been analyzed (Phase 5).
    /// </summary>
    public void RunGlobal()
    {
        var generator = new ErrorHandlingGenerator(registry: ctx.Registry);

        // Snapshot before iteration — registering variants adds new routines to the registry
        var routines = ctx.Registry.GetAllRoutines().ToList();

        foreach (RoutineInfo routine in routines)
        {
            if (!routine.IsFailable) continue;

            if (!ctx.RoutineBodies.TryGetValue(key: routine.RegistryKey, value: out Statement? body))
                continue;

            GenerateVariantsForRoutine(generator: generator, routine: routine, body: body);
        }
    }

    private void GenerateVariantsForRoutine(ErrorHandlingGenerator generator,
        RoutineInfo routine, Statement body)
    {
        // @crash_only: still analyze throw/absent but suppress safe variant generation
        if (routine.Annotations.Any(predicate: a => a == "crash_only"))
        {
            ErrorHandlingResult crashOnlyResult =
                generator.GenerateVariants(routine: routine, body: body);
            routine.HasThrow = crashOnlyResult.HasThrow;
            routine.HasAbsent = crashOnlyResult.HasAbsent;
            return;
        }

        ErrorHandlingResult result = generator.GenerateVariants(routine: routine, body: body);

        // If generation fails (e.g., @llvm_ir routines with no throw/absent AST nodes),
        // skip — no error reported. External implementations don't need generated variants.
        if (result.Error != null) return;

        routine.HasThrow = result.HasThrow;
        routine.HasAbsent = result.HasAbsent;
        routine.ThrowableTypes = result.ThrownTypes;

        foreach (GeneratedVariant variant in result.Variants)
        {
            ctx.Registry.RegisterRoutine(routine: variant.Routine);
            // Propagate thrown types to check_/lookup_ variants so the
            // CrashableExpansionPass can enumerate them at the call site.
            variant.Routine.ThrowableTypes = result.ThrownTypes;

            // Build a pre-transformed body for this variant so codegen can emit carrier
            // construction without relying on mutable _currentVariantIs* flags.
            ErrorHandlingVariantKind kind = DetermineVariantKind(variant: variant);
            Statement variantBody = TransformBody(body: body, kind: kind);
            ctx.VariantBodies[key: variant.Routine.RegistryKey] = variantBody;
        }
    }

    /// <summary>
    /// Maps a <see cref="GeneratedVariant"/> to its <see cref="ErrorHandlingVariantKind"/>,
    /// including distinguishing the TryBool case (Blank-returning try_ variant).
    /// </summary>
    private static ErrorHandlingVariantKind DetermineVariantKind(GeneratedVariant variant)
    {
        return variant.Kind switch
        {
            ErrorHandlingVariantKind.Try when variant.Routine.AsyncStatus == AsyncStatus.TryBoolVariant
                => ErrorHandlingVariantKind.TryBool,
            _ => variant.Kind
        };
    }

    /// <summary>
    /// Recursively walks a routine body and replaces throw/absent/return statements with
    /// <see cref="VariantReturnStatement"/> nodes appropriate for the given variant kind.
    /// All other statements are passed through unchanged (structurally cloned via record-with).
    /// </summary>
    private static Statement TransformBody(Statement body, ErrorHandlingVariantKind kind)
    {
        return body switch
        {
            ThrowStatement ts =>
                new VariantReturnStatement(kind, VariantSiteKind.FromThrow, ts.Error, ts.Location),

            AbsentStatement abs =>
                new VariantReturnStatement(kind, VariantSiteKind.FromAbsent, null, abs.Location),

            ReturnStatement ret =>
                new VariantReturnStatement(kind, VariantSiteKind.FromReturn, ret.Value, ret.Location),

            BlockStatement block => block with
            {
                Statements = block.Statements
                                  .Select(selector: s => TransformBody(body: s, kind: kind))
                                  .ToList()
            },

            IfStatement ifs => ifs with
            {
                ThenStatement = TransformBody(body: ifs.ThenStatement, kind: kind),
                ElseStatement = ifs.ElseStatement != null
                    ? TransformBody(body: ifs.ElseStatement, kind: kind)
                    : null
            },

            WhileStatement ws => ws with
            {
                Body = TransformBody(body: ws.Body, kind: kind),
                ElseBranch = ws.ElseBranch != null
                    ? TransformBody(body: ws.ElseBranch, kind: kind)
                    : null
            },

            ForStatement fs => fs with
            {
                Body = TransformBody(body: fs.Body, kind: kind),
                ElseBranch = fs.ElseBranch != null
                    ? TransformBody(body: fs.ElseBranch, kind: kind)
                    : null
            },

            WhenStatement ws => ws with
            {
                Clauses = ws.Clauses
                            .Select(selector: c => c with
                             {
                                 Body = TransformBody(body: c.Body, kind: kind)
                             })
                            .ToList()
            },

            UsingStatement us => us with
            {
                Body = TransformBody(body: us.Body, kind: kind)
            },

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)TransformBody(body: danger.Body, kind: kind)
            },

            _ => body // All other statements pass through unchanged
        };
    }
}
