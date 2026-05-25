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

        // Phase C: register all variants first (no body transformation yet) — so the body
        // rewriter in Phase D can find variants of callees regardless of iteration order.
        var pending = new List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)>();
        foreach (RoutineInfo routine in routines)
        {
            if (!routine.IsFailable) continue;
            if (!ctx.RoutineBodies.TryGetValue(key: routine.RegistryKey, value: out Statement? body))
                continue;

            // @crash_only: still analyze throw/absent but suppress safe variant generation
            if (routine.Annotations.Any(predicate: a => a == "crash_only"))
            {
                ErrorHandlingResult crashOnlyResult =
                    generator.GenerateVariants(routine: routine, body: body);
                routine.HasThrow = crashOnlyResult.HasThrow;
                routine.HasAbsent = crashOnlyResult.HasAbsent;
                continue;
            }

            ErrorHandlingResult result = generator.GenerateVariants(routine: routine, body: body);
            if (result.Error != null) continue;

            routine.HasThrow = result.HasThrow;
            routine.HasAbsent = result.HasAbsent;
            routine.ThrowableTypes = result.ThrownTypes;

            foreach (GeneratedVariant variant in result.Variants)
            {
                ctx.Registry.RegisterRoutine(routine: variant.Routine);
                variant.Routine.ThrowableTypes = result.ThrownTypes;
            }

            pending.Add((routine, body, result.Variants));
        }

        // Phase D: now that all variants are registered, transform each body — rewriter can
        // find variants of inner failable calls and substitute them.
        foreach ((RoutineInfo routine, Statement body, List<GeneratedVariant> variants) in pending)
        {
            foreach (GeneratedVariant variant in variants)
            {
                ErrorHandlingVariantKind kind = DetermineVariantKind(variant: variant);
                Statement variantSourceBody = GenericAstRewriter.RewriteStatement(
                    stmt: body,
                    subs: new Dictionary<string, string>());
                Statement variantBody = TransformBody(body: variantSourceBody, kind: kind, rewriter: TryRewriteToVariantCall);
                ctx.VariantBodies[key: variant.Routine.RegistryKey] = variantBody;
            }
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
    /// Signature for an optional rewriter that may convert a tail-return value into a passthrough
    /// call against the corresponding try_/check_/lookup_ variant of an inner failable callee.
    /// </summary>
    public delegate bool VariantCallRewriter(Expression? value, ErrorHandlingVariantKind kind, out Expression? rewritten);

    /// <summary>
    /// Recursively walks a routine body and replaces throw/absent/return statements with
    /// <see cref="VariantReturnStatement"/> nodes appropriate for the given variant kind.
    /// All other statements are passed through unchanged (structurally cloned via record-with).
    /// When <paramref name="rewriter"/> succeeds on a tail-position return value, the value is
    /// emitted as <see cref="VariantSiteKind.FromVariantPassthrough"/> so codegen returns the
    /// already-carrier-shaped expression directly.
    /// </summary>
    internal static Statement TransformBody(Statement body, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter = null)
    {
        return body switch
        {
            ThrowStatement ts =>
                new VariantReturnStatement(kind, VariantSiteKind.FromThrow, ts.Error, ts.Location),

            AbsentStatement abs =>
                new VariantReturnStatement(kind, VariantSiteKind.FromAbsent, null, abs.Location),

            ReturnStatement ret when rewriter != null && rewriter(ret.Value, kind, out Expression? vcall) =>
                new VariantReturnStatement(kind, VariantSiteKind.FromVariantPassthrough, vcall, ret.Location),

            ReturnStatement ret =>
                new VariantReturnStatement(kind, VariantSiteKind.FromReturn, ret.Value, ret.Location),

            BlockStatement block => block with
            {
                Statements = block.Statements
                                  .Select(selector: s => TransformBody(body: s, kind: kind, rewriter: rewriter))
                                  .ToList()
            },

            IfStatement ifs => ifs with
            {
                ThenStatement = TransformBody(body: ifs.ThenStatement, kind: kind, rewriter: rewriter),
                ElseStatement = ifs.ElseStatement != null
                    ? TransformBody(body: ifs.ElseStatement, kind: kind, rewriter: rewriter)
                    : null
            },

            WhileStatement ws => ws with
            {
                Body = TransformBody(body: ws.Body, kind: kind, rewriter: rewriter),
                ElseBranch = ws.ElseBranch != null
                    ? TransformBody(body: ws.ElseBranch, kind: kind, rewriter: rewriter)
                    : null
            },

            ForStatement fs => fs with
            {
                Body = TransformBody(body: fs.Body, kind: kind, rewriter: rewriter),
                ElseBranch = fs.ElseBranch != null
                    ? TransformBody(body: fs.ElseBranch, kind: kind, rewriter: rewriter)
                    : null
            },

            WhenStatement ws => ws with
            {
                Clauses = ws.Clauses
                            .Select(selector: c => c with
                             {
                                 Body = TransformBody(body: c.Body, kind: kind, rewriter: rewriter)
                             })
                            .ToList()
            },

            UsingStatement us => us with
            {
                Body = TransformBody(body: us.Body, kind: kind, rewriter: rewriter)
            },

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)TransformBody(body: danger.Body, kind: kind, rewriter: rewriter)
            },

            _ => body // All other statements pass through unchanged
        };
    }

    /// <summary>
    /// If <paramref name="value"/> is a tail-position call to a failable routine and a matching
    /// variant exists in the registry, returns a rewritten call that targets the variant.
    /// The rewritten call's resolved routine is the variant (non-failable) so codegen does not
    /// emit a throw-propagating call site.
    /// </summary>
    private bool TryRewriteToVariantCall(Expression? value, ErrorHandlingVariantKind kind, out Expression? rewritten)
    {
        rewritten = null;
        if (value == null) return false;

        string? prefix = kind switch
        {
            ErrorHandlingVariantKind.Try => "try",
            ErrorHandlingVariantKind.Check => "check",
            ErrorHandlingVariantKind.Lookup => "lookup",
            _ => null
        };
        if (prefix == null) return false;

        if (value is CallExpression { ResolvedRoutine: { IsFailable: true } callee } call)
        {
            RoutineInfo? variant = FindVariant(original: callee, prefix: prefix);
            if (variant == null) return false;

            CallExpression newCall = call with { ResolvedRoutine = variant };
            newCall = newCall.Callee switch
            {
                IdentifierExpression idCallee => newCall with { Callee = idCallee with { Name = variant.Name } },
                MemberExpression memCallee => newCall with { Callee = memCallee with { PropertyName = variant.Name } },
                _ => newCall
            };
            rewritten = newCall;
            return true;
        }

        if (value is CreatorExpression { ResolvedCreatorRoutine: { IsFailable: true } cCallee } creator)
        {
            RoutineInfo? variant = FindVariant(original: cCallee, prefix: prefix);
            if (variant == null) return false;

            var typeId = new IdentifierExpression(Name: creator.TypeName, Location: creator.Location);
            var member = new MemberExpression(Object: typeId, PropertyName: variant.Name, Location: creator.Location);
            var args = creator.MemberVariables
                .Select(selector: mv => (Expression)new NamedArgumentExpression(
                    Name: mv.Name, Value: mv.Value, Location: creator.Location))
                .ToList();
            var newCall = new CallExpression(Callee: member, Arguments: args, Location: creator.Location)
            {
                ResolvedRoutine = variant
            };
            rewritten = newCall;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the variant routine for <paramref name="original"/> with the given prefix
    /// (try/check/lookup). Matches by <see cref="RoutineInfo.OriginalName"/> + owner identity.
    /// </summary>
    private RoutineInfo? FindVariant(RoutineInfo original, string prefix)
    {
        string baseName = original.Name.TrimStart(trimChar: '$');
        string variantName = $"{prefix}_{baseName}";
        foreach (RoutineInfo r in ctx.Registry.GetAllRoutines())
        {
            if (r.Name != variantName) continue;
            if (r.OriginalName != original.Name) continue;
            if (!ReferenceEquals(objA: r.OwnerType, objB: original.OwnerType)) continue;
            if (r.Parameters.Count != original.Parameters.Count) continue;
            bool allMatch = true;
            for (int i = 0; i < r.Parameters.Count; i++)
            {
                if (r.Parameters[index: i].Type.FullName != original.Parameters[index: i].Type.FullName)
                {
                    allMatch = false;
                    break;
                }
            }
            if (!allMatch) continue;
            return r;
        }
        return null;
    }
}
