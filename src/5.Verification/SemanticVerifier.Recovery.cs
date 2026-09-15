using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 5: whole-expression <c>try</c>/<c>grab</c>/<c>lookup</c> recovery composition.
///
/// A recovery keyword wraps an ENTIRE expression and short-circuits to its carrier on the FIRST failure
/// (evaluation order: inner→outer, left→right). Rather than lower this inline (which, lacking a non-local
/// break, would nest a <c>when</c> per hop), we synthesize a failable BASE routine
/// <c>__recover_N!(freevars) -&gt; T</c> whose body is the inner expression in A-normal form (each failable
/// sub-call hoisted to its own <c>var</c>, in evaluation order), then take that base's
/// <c>try_</c>/<c>check_</c>/<c>lookup_</c> variant. The variant's <see cref="Builder.Instantiation.ErrorHandlingVariantPass.TransformBody"/>
/// turns every hoisted failable <c>var</c> into its own recovery variant + unwrap + early <c>return</c> —
/// i.e. the existing per-routine variant machinery does all the threading. The <see cref="RecoveryExpression"/>'s
/// <see cref="RecoveryExpression.LoweredCall"/> becomes a call to that variant (Phase-6 splices it in).
///
/// An expression with NO failable call degenerates naturally: the base body is just <c>return Inner</c>, and
/// its variant wraps the value into an always-present carrier — no error, nothing to recover.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Monotonic sequence for uniquely naming synthesized recovery base routines per compile.</summary>
    private int _recoveryCompositionSeq;

    /// <summary>Prefix for hoisted per-sub-call temporaries in a composition base body.</summary>
    private const string RecoveryTempPrefix = "__rc_t";

    /// <summary>
    /// Analyzes a <c>try</c>/<c>grab</c>/<c>lookup</c> <see cref="RecoveryExpression"/> as a whole-expression
    /// composition (see the class remarks). Returns the carrier type (Maybe/Check/Lookup[T], None-collapsed);
    /// stashes the synthesized variant call on <see cref="RecoveryExpression.LoweredCall"/>.
    /// </summary>
    private TypeSymbol AnalyzeRecoveryExpression(RecoveryExpression recovery)
    {
        // Analyze the inner expression first so every sub-call carries its ResolvedRoutine + ResolvedType;
        // the decomposition below keys failable-ness off ResolvedRoutine.IsFailable.
        TypeSymbol innerType = AnalyzeExpression(expression: recovery.Inner);
        if (innerType is ErrorTypeSymbol)
        {
            return ErrorTypeSymbol.Instance;
        }

        // Decompose the inner expression into A-normal form: hoist each failable sub-call (post-order =
        // evaluation order) into `var __rc_tN = <call>`, replacing the call with a reference to the temp.
        // Referenced identifiers are collected in the SAME walk so we can pass the outer locals the body
        // reads as parameters of the synthesized routine.
        var hoister = new RecoveryFailableHoister(seed: 0);
        Expression residual = hoister.VisitExpression(expr: recovery.Inner);

        // Free variables = referenced identifiers that resolve to an outer local/param (not a hoisted temp,
        // not a type/routine/global). These become the base routine's parameters.
        var freeParams = new List<ParamInfo>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string name in hoister.ReferencedNames)
        {
            if (name.StartsWith(value: RecoveryTempPrefix, comparisonType: StringComparison.Ordinal) ||
                !seen.Add(item: name))
            {
                continue;
            }

            VariableInfo? local = _registry.LookupVariable(name: name);
            if (local != null)
            {
                freeParams.Add(item: new ParamInfo(name: name, type: local.Type));
            }
        }

        // Build the base routine's body: the hoisted failable-call decls, then `return residual`.
        var bodyStmts = new List<Statement>(collection: hoister.Hoisted)
        {
            new ReturnStatement(Value: residual, Location: recovery.Location)
        };
        var baseBody = new BlockStatement(Statements: bodyStmts, Location: recovery.Location);

        // Synthesize the failable base routine __recover_N! and register it. It is a TEMPLATE — never called
        // directly (only its recovery variant is), so it is never collected/emitted on its own.
        // The carrier the KEYWORD selects drives which variant the base must expose:
        //  - try    → try_    (Maybe): pessimistic (throw+absent) generates try_.
        //  - lookup → lookup_ (Lookup): pessimistic (throw+absent) generates lookup_.
        //  - grab   → check_  (Check): grab collapses the WHOLE crashable set (and, per the design, promotes
        //    a sub-call's absent to AbsentValueError) into Check's single Crashable arm. The variant rules
        //    only mint check_ for a THROW-ONLY shape, so mark grab's base throw-only (HasThrow, no HasAbsent,
        //    non-pessimistic) — otherwise the both-shape yields lookup_ and no check_ exists.
        bool grab = recovery.Kind == RecoveryKind.Grab;
        string baseName = $"__recover_{_recoveryCompositionSeq++}";
        var baseRoutine = new RoutineInfo(name: baseName)
        {
            Kind = RoutineKind.FreeRoutine,
            Parameters = freeParams,
            ReturnType = innerType,
            IsFailable = true,
            IsSynthesized = true,
            HasThrow = grab,
            // Open: this is compiler-internal, and its variant inherits this visibility. Secret would trip
            // the cross-module access check (RF-S403) at the call site.
            Visibility = VisibilityModifier.Open,
            Location = recovery.Location,
            Module = _currentRoutine?.Module,
            ModulePath = _currentRoutine?.ModulePath
        };
        _registry.RegisterRoutine(routine: baseRoutine);

        // Index the base for on-demand variant synthesis. try/lookup use the pessimistic (throw+absent)
        // shape; grab uses the throw-only shape stamped above (so check_ is generated).
        _registry.DeferredVariantBases[key: baseRoutine.RegistryKey] = (baseRoutine, baseBody, !grab);

        string prefix = recovery.Kind switch
        {
            RecoveryKind.Grab => "check",
            RecoveryKind.Lookup => "lookup",
            _ => "try"
        };

        RoutineInfo? variant = SynthesizeVariantForBase(baseOverload: baseRoutine, prefix: prefix);
        if (variant == null)
        {
            // The requested carrier variant is not (yet) synthesizable for this base shape (e.g. `grab`'s
            // check_ before the Check/Lookup propagation generalization). Surface as a generation error
            // rather than silently miscompiling.
            ReportError(code: SemanticDiagnosticCode.VariantGenerationError,
                message:
                $"'{RecoveryKeyword(kind: recovery.Kind)}' recovery composition could not synthesize its " +
                $"carrier variant for this expression.",
                location: recovery.Location);
            return ErrorTypeSymbol.Instance;
        }

        // The LoweredCall is a plain free call to the (uniquely-named) variant, passing the captured locals
        // as named arguments. Re-analyzing it stamps ResolvedRoutine/LoweringKind/ResolvedType the normal way.
        List<Expression> args = freeParams
                                .Select(selector: p => (Expression)new NamedArgumentExpression(
                                    Name: p.Name,
                                    Value: new IdentifierExpression(Name: p.Name,
                                        Location: recovery.Location),
                                    Location: recovery.Location))
                                .ToList();
        var loweredCall = new CallExpression(
            Callee: new IdentifierExpression(Name: variant.Name, Location: recovery.Location),
            Arguments: args,
            Location: recovery.Location);
        TypeSymbol carrierType = AnalyzeExpression(expression: loweredCall);
        recovery.LoweredCall = loweredCall;
        return carrierType;
    }

    /// <summary>The surface keyword for a <see cref="RecoveryKind"/>, for diagnostics.</summary>
    private static string RecoveryKeyword(RecoveryKind kind) => kind switch
    {
        RecoveryKind.Grab => "grab",
        RecoveryKind.Lookup => "lookup",
        _ => "try"
    };

    /// <summary>
    /// An <see cref="AstRewriter"/> that rewrites an expression into A-normal form for recovery composition:
    /// every failable <see cref="CallExpression"/> (post-order = evaluation order) is hoisted into a fresh
    /// <c>var __rc_tN = &lt;call&gt;</c> declaration (collected in <see cref="Hoisted"/>) and replaced in the
    /// tree by a reference to that temp. All identifier names encountered are recorded in
    /// <see cref="ReferencedNames"/> so the caller can capture the outer locals the body reads as parameters.
    /// </summary>
    private sealed class RecoveryFailableHoister(int seed) : AstRewriter
    {
        private int _seq = seed;

        /// <summary>The hoisted `var __rc_tN = &lt;failable call&gt;` declarations, in evaluation order.</summary>
        public List<Statement> Hoisted { get; } = [];

        /// <summary>Every identifier name seen while walking the expression.</summary>
        public HashSet<string> ReferencedNames { get; } = new(comparer: StringComparer.Ordinal);

        public override Expression VisitExpression(Expression expr)
        {
            if (expr is IdentifierExpression id)
            {
                ReferencedNames.Add(item: id.Name);
            }

            return base.VisitExpression(expr: expr);
        }

        protected override Expression VisitCall(CallExpression e)
        {
            // Rewrite children first (post-order) so any inner failable calls are already hoisted + replaced.
            Expression rewritten = base.VisitCall(e: e);
            // A sub-call is failable when its resolved routine is failable. IsFailable is only DERIVED from
            // HasThrow/HasAbsent by a later pass, so during Phase-5 an INFERRED-failable callee (a body that
            // `throw`/`absent`s with no explicit `!`, already analyzed before this call site) carries
            // HasThrow/HasAbsent but not yet IsFailable — check all three.
            if (rewritten is not CallExpression { ResolvedRoutine: { } rr } call ||
                !(rr.IsFailable || rr.HasThrow || rr.HasAbsent))
            {
                return rewritten;
            }

            string tempName = $"{RecoveryTempPrefix}{_seq++}";
            var decl = new VariableDeclaration(Name: tempName,
                Type: null,
                Initializer: call,
                Visibility: VisibilityModifier.Secret,
                Location: call.Location);
            Hoisted.Add(item: new DeclarationStatement(Declaration: decl, Location: call.Location));

            return new IdentifierExpression(Name: tempName, Location: call.Location)
            {
                ResolvedType = call.ResolvedType
            };
        }
    }
}
