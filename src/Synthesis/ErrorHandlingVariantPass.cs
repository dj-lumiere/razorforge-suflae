using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Instantiation;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Synthesis;


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
    /// <summary>Per-file stub -> variant generation is global only.</summary>
    public static void Run(Program program) { }

    /// <summary>
    /// Runs variant generation globally.
    /// Must be called once after all routine bodies have been analyzed (Phase 5).
    /// </summary>
    public void RunGlobal()
    {
        var generator = new ErrorHandlingGenerator(registry: ctx.Registry);

        // Snapshot before iteration -> registering variants adds new routines to the registry
        var routines = ctx.Registry.GetAllRoutines().ToList();

        // Phase A: populate per-routine HasThrow/HasAbsent/ThrowableTypes from direct body
        // scan. (Verifier sets HasThrow/HasAbsent for direct cases; we also need ThrowableTypes
        // populated before propagation can fan them out through the call graph.)
        foreach (RoutineInfo routine in routines)
        {
            if (!routine.IsFailable) continue;
            if (!ctx.RoutineBodies.TryGetValue(key: routine.RegistryKey,
                    value: out Statement? body)) continue;

            ErrorHandlingAnalysis analysis = ErrorHandlingGenerator.AnalyzeBody(body);
            if (analysis.HasThrow) routine.HasThrow = true;
            if (analysis.HasAbsent) routine.HasAbsent = true;
            foreach (TypeInfo t in analysis.ThrownTypes)
            {
                if (!routine.ThrowableTypes.Contains(t)) routine.ThrowableTypes.Add(t);
            }
        }

        // Phase A2: stdlib bodies are stored by CollectStdlibBodiesForVariantGeneration
        // without running SA, so propagated-failability routines (e.g. stdlib
        // `common routine S64.from_digit_bytes!` returning `S64.from_digit_bytes_at!`) have
        // empty FailableCallees and no direct throw/absent. Detect them and mark pessimistic
        // so variant generation produces try_ + lookup_ — matching what the pre-register
        // pass registered as stubs.
        foreach (RoutineInfo routine in routines)
        {
            if (!routine.IsFailable) continue;
            if (routine.HasThrow || routine.HasAbsent) continue;
            if (routine.FailableCallees.Count > 0) continue;
            if (!ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;
            routine.HasThrow = true;
            routine.HasAbsent = true;
        }

        // Phase B: fixpoint propagation through FailableCallees. A routine whose failability
        // is purely propagated (e.g. `routine S64_from_text!(t: Text) -> S64
        // return S64!(from_text: t)`) has HasThrow=HasAbsent=false but FailableCallees={S64.$create!}.
        // We OR the callees' state into the caller until no further change.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (RoutineInfo routine in routines)
            {
                if (!routine.IsFailable) continue;
                foreach (RoutineInfo callee in routine.FailableCallees)
                {
                    if (callee.HasThrow && !routine.HasThrow)
                    {
                        routine.HasThrow = true;
                        changed = true;
                    }
                    if (callee.HasAbsent && !routine.HasAbsent)
                    {
                        routine.HasAbsent = true;
                        changed = true;
                    }
                    foreach (TypeInfo t in callee.ThrowableTypes)
                    {
                        if (!routine.ThrowableTypes.Contains(t))
                        {
                            routine.ThrowableTypes.Add(t);
                            changed = true;
                        }
                    }
                }
            }
        }

        // Phase C: variant generation (now reads propagated HasThrow/HasAbsent/ThrowableTypes).
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
        // skip -> no error reported. External implementations don't need generated variants.
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
            Statement variantSourceBody = GenericAstRewriter.RewriteStatement(
                stmt: body,
                subs: new Dictionary<string, string>());
            Statement variantBody = TransformBody(body: variantSourceBody, kind: kind);
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
    internal static Statement TransformBody(Statement body, ErrorHandlingVariantKind kind)
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
