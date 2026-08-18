using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using System.Text;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 5: Error handling variant support.
///
/// RazorForge/Suflae error handling model:
/// - Failable functions end with ! suffix (e.g., parse!, connect!)
/// - throw statement: signals a failure with an error value
/// - absent statement: signals "not found" without error
///
/// Variant generation rules:
/// - Only absent: try_ (returns T? -> None on absent)
/// - Only throw: try_ (returns T? -> None on throw) + check_ (returns Result&lt;T&gt;)
/// - Both throw and absent: try_ + lookup_ (returns Lookup&lt;T&gt;)
///
/// The actual variant generation is delegated to <see cref="ErrorHandlingVariantPass"/>
/// which runs in Phase 6 (global desugaring) after body analysis populates <c>_routineBodies</c>.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 5: Error Handling Body Collection

    /// <summary>
    /// Storage for routine bodies needed during variant generation.
    /// Maps RoutineInfo.RegistryKey to its body statement.
    /// Populated during body analysis; consumed by ErrorHandlingVariantPass in Phase 6.
    /// </summary>
    private readonly Dictionary<string, Statement> _routineBodies = new();

    /// <summary>
    /// Stores a routine body for later variant generation.
    /// Called during Phase 4 body analysis.
    /// </summary>
    /// <param name="routine">The routine whose body is being stored.</param>
    /// <param name="body">The routine's body statement.</param>
    private void StoreRoutineBody(RoutineInfo routine, Statement body)
    {
        _routineBodies[key: routine.RegistryKey] = body;
    }

    /// <summary>
    /// Phase 6 pre-pass: Pre-register error handling variant stubs for user-defined failable routines.
    /// Called before Phase 5 body analysis so that try_/check_/lookup_ variants are in scope
    /// when user code calls them from within the same module.
    /// Uses AST-level throw/absent detection -> no full semantic analysis required.
    /// </summary>
    internal void PreRegisterUserVariants(Program program) // NOSONAR S3776
    {
        var generator = new ErrorHandlingGenerator(registry: _registry);
        string? currentModule = GetCurrentModuleName();

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not RoutineDeclaration routineDecl || !routineDecl.IsFailable ||
                routineDecl.Body == null)
            {
                continue;
            }

            // AST scan: routines with direct throw/absent get precise variants;
            // routines without any (propagated-failability via called `!` routines) get
            // pessimistic try_+lookup_ stubs so callsites can resolve them during SA.
            bool hasDirect = generator.BodyHasThrowOrAbsent(body: routineDecl.Body);

            RoutineInfo? routineInfo =
                ResolveRoutineInfoForDeclaration(decl: routineDecl, moduleName: currentModule);
            if (routineInfo == null || !routineInfo.IsFailable) continue;
            if (routineInfo.Annotations.Any(predicate: a => a == "crash_only")) continue;

            ErrorHandlingResult result = generator.GenerateVariants(
                routine: routineInfo, body: routineDecl.Body, pessimistic: !hasDirect);
            if (result.Error != null) continue;

            foreach (GeneratedVariant variant in result.Variants)
            {
                CheckReservedVariantCollision(baseRoutine: routineInfo, variant: variant.Routine);
                _registry.RegisterRoutine(routine: variant.Routine);
            }
        }
    }

    /// <summary>Variant RegistryKeys already reported as collisions, to avoid duplicate RF-S409s
    /// when a pre-register pass runs over the same routine set more than once.</summary>
    private readonly HashSet<string> _reportedVariantCollisions = new();

    /// <summary>
    /// Reports RF-S409 when a hand-declared routine already occupies the exact slot
    /// (owner + name + signature) the compiler synthesizes for a failable variant
    /// (<c>try_</c>/<c>check_</c>/<c>lookup_</c>). The <see cref="RoutineInfo.RegistryKey"/>
    /// match is uniform across member and free routines. Only a genuine collision counts: a
    /// hand-written <c>try_lock</c> with no failable <c>lock!</c> base generates no variant, so
    /// it never reaches here — the reserved prefixes cost nothing until a colliding failable
    /// routine actually exists.
    /// </summary>
    private void CheckReservedVariantCollision(RoutineInfo baseRoutine, RoutineInfo variant)
    {
        // The variant hasn't been registered yet, so any occupant of its key is pre-existing.
        // Synthesized occupants (e.g. a stub from another pre-register pass) aren't collisions —
        // RegisterRoutine never lets a synthesized routine overwrite a user-written one, so a
        // non-synthesized occupant means a real hand-declared clash.
        string key = variant.RegistryKey;
        if (_registry.GetRoutineByExactKey(registryKey: key) is not { IsSynthesized: false } handWritten)
            return;

        SourceLocation? location = handWritten.Location ?? baseRoutine.Location;
        if (location == null || !_reportedVariantCollisions.Add(item: key)) return;

        ReportError(code: SemanticDiagnosticCode.ReservedRoutinePrefix,
            message:
            $"'{variant.Name}' collides with the variant the compiler generates for failable " +
            $"'{baseRoutine.Name}!'; the try_/check_/lookup_ prefixes are reserved for " +
            "compiler-generated failable variants — rename this routine",
            location: location);
    }

    /// <summary>
    /// Phase 3 global: pre-registers try_/check_/lookup_ stub variants for all failable stdlib
    /// member routines (e.g., Tracked[T].recover!, ListEmitter[T].emit!).
    /// Must run before Phase 4 user-body analysis so that user code calling these variants
    /// (e.g., <c>rt.try_recover()</c> or desugared for-loop <c>iter.try_emit()</c>) resolves
    /// without S450. Mirrors <see cref="PreRegisterUserVariants"/> but for stdlib programs.
    /// </summary>
    private void PreRegisterStdlibVariants()
    {
        var generator = new ErrorHandlingGenerator(registry: _registry);

        foreach ((Program program, _, string module) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || !decl.IsFailable || decl.Body == null)
                    continue;

                bool hasDirect = generator.BodyHasThrowOrAbsent(body: decl.Body);

                RoutineInfo? routineInfo = ResolveRoutineInfoForDeclaration(decl: decl, moduleName: module);
                if (routineInfo == null || !routineInfo.IsFailable) continue;
                if (routineInfo.Annotations.Any(predicate: a => a == "crash_only")) continue;

                ErrorHandlingResult result = generator.GenerateVariants(
                    routine: routineInfo, body: decl.Body, pessimistic: !hasDirect);
                if (result.Error != null) continue;

                foreach (GeneratedVariant variant in result.Variants)
                {
                    CheckReservedVariantCollision(baseRoutine: routineInfo, variant: variant.Routine);
                    _registry.RegisterRoutine(routine: variant.Routine);
                }
            }
        }

    }

    /// <summary>
    /// Collects stdlib member-routine bodies into <c>_routineBodies</c> keyed by
    /// <see cref="RoutineInfo.RegistryKey"/>. Stdlib routines aren't semantically analyzed
    /// (only registered), so <c>_routineBodies</c> would otherwise contain only user-side
    /// failable routines. Downstream passes need stdlib bodies too:
    /// <see cref="ErrorHandlingVariantPass"/> for failable iterators (e.g.
    /// <c>ListEmitter[T].emit!</c>) and <see cref="Compiler.Instantiation.Passes.ProtocolDefaultImplLoweringPass"/>
    /// for protocol-extension routines (e.g. <c>Iterable[Text].join</c>).
    /// Called before RunPhase4GlobalDesugaring() so the bodies are visible to both phases.
    /// </summary>
    private void CollectStdlibBodiesForVariantGeneration()
    {
        foreach ((Program program, _, string module) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || decl.Body == null)
                    continue;

                // Only member routines: standalone free routines aren't candidates for
                // either variant generation or protocol-default-impl monomorphization.
                if (!decl.Name.Contains('.'))
                    continue;

                // Auto-derive template: `@overridable/@override routine T.method()`. Captured
                // straight from the decl (owner param + kind gates + body), NOT via a resolved
                // RoutineInfo — the owner is a type-parameter placeholder that doesn't resolve, and
                // several same-signature kind-gated templates must coexist in the derive-template
                // store, which the signature-keyed registry cannot hold.
                if (decl.Annotations.Contains(item: "overridable")
                    || decl.Annotations.Contains(item: "override"))
                {
                    int dot = decl.Name.IndexOf(value: '.');
                    _registry.RegisterDeriveTemplate(method: decl.Name[(dot + 1)..],
                        ownerParam: decl.Name[..dot],
                        arity: decl.Parameters.Count,
                        constraints: decl.GenericConstraints,
                        body: decl.Body);
                }

                RoutineInfo? routineInfo = ResolveRoutineInfoForDeclaration(decl: decl, moduleName: module);
                if (routineInfo == null) continue;

                if (!_routineBodies.ContainsKey(key: routineInfo.RegistryKey))
                    _routineBodies[key: routineInfo.RegistryKey] = decl.Body;
            }
        }
    }

    private static bool LooksLikeGenericParamArg(string ownerTypeName)
    {
        int lb = ownerTypeName.IndexOf('[');
        int rb = ownerTypeName.LastIndexOf(']');
        if (lb < 0 || rb < 0 || rb <= lb) return false;
        string inside = ownerTypeName.Substring(lb + 1, rb - lb - 1);
        foreach (string arg in inside.Split(','))
        {
            string a = arg.Trim();
            if (a.Length == 0) return false;
            if (a.Length > 2) return false; // T, K, V, N — single/double upper letters
            if (!char.IsUpper(a[0])) return false;
            if (a.Length == 2 && !char.IsLetterOrDigit(a[1])) return false;
        }
        return true;
    }

    private RoutineInfo? ResolveRoutineInfoForDeclaration(RoutineDeclaration decl, string? moduleName = null)
    {
        if (decl.Name.Contains('.'))
        {
            int dotIdx = decl.Name.LastIndexOf('.');
            string ownerTypeName = decl.Name[..dotIdx];
            string methodName = decl.Name[(dotIdx + 1)..];

            // Stdlib protocol-extension decls like `Iterable[Text].join` register their routines
            // under the bracketed-owner bucket (FullName = "Core.Iterable[Text]"). Try the
            // bracketed form first, falling back to the gen-def name. Both lookups can succeed
            // on different types: prefer the one that actually has the candidate method.
            string bareLookupName = TypeInfo.StripTypeArgs(name: ownerTypeName);

            // Own-module FIRST: a member decl `routine List[T].add_last` in `module Suflae` owns
            // `Suflae.List`, not the earlier-registered context-free `Core.List`. Resolving bare first
            // collected candidates only from `Core.List`, so the Suflae overlay's method body was keyed
            // under (and monomorphized as) Core.List's — the wrapper forwarder body was lost, and the
            // Suflae instantiation ran Core.List's `add_last` (with its `reserve` call) instead.
            TypeSymbol? bareOwner = (moduleName != null
                                        ? _registry.LookupType(name: $"{moduleName}.{bareLookupName}")
                                        : null)
                                    ?? _registry.LookupType(name: bareLookupName);
            if (bareOwner == null)
            {
                return null;
            }

            var candidates = new List<RoutineInfo>();
            _registry.CollectMemberRoutineCandidates(type: bareOwner, methodName: methodName,
                candidates: candidates);

            // Protocol-extension decls like `Iterable[Text].join` register their routines under
            // a bracketed-owner bucket (e.g. owner FullName="Core.Iterable[Text]") that the
            // gen-def lookup misses. Scan all routines for owners whose name shape matches the
            // bracketed form.
            if (ownerTypeName.Contains('[') && !LooksLikeGenericParamArg(ownerTypeName))
            {
                TypeSymbol? bracketed = _registry.LookupType(name: ownerTypeName);
                if (bracketed != null && !ReferenceEquals(bracketed, bareOwner))
                {
                    _registry.CollectMemberRoutineCandidates(type: bracketed, methodName: methodName,
                        candidates: candidates);
                }
            }
            // For member-routine decls, prefer the decl's actual module (passed in) over the
            // owner type's module: common routines for built-in types (e.g. `S64.from_digit_bytes`
            // declared in `IO/BytesIO`) live in a different module from the owner.
            return MatchRoutineDeclaration(candidates: candidates, decl: decl,
                moduleName: moduleName ?? bareOwner.Module);
        }

        string bareName = decl.Name;
        string qualifiedName = string.IsNullOrEmpty(value: moduleName)
            ? bareName
            : $"{moduleName}.{bareName}";

        var standaloneCandidates = _registry.GetAllRoutines()
                                            .Where(routine => routine.OwnerType == null &&
                                                              routine.Name == bareName &&
                                                              (string.IsNullOrEmpty(moduleName) || routine.Module == moduleName ||
                                                               routine.BaseName == qualifiedName))
                                            .ToList();
        return MatchRoutineDeclaration(candidates: standaloneCandidates, decl: decl,
            moduleName: moduleName);
    }

    private static RoutineInfo? MatchRoutineDeclaration(List<RoutineInfo> candidates,
        RoutineDeclaration decl, string? moduleName)
    {
        static string NormalizeTypeName(string name)
        {
            name = name.Replace(oldValue: " ", newValue: "");
            var sb = new StringBuilder(name.Length);
            var token = new StringBuilder();

            static void FlushToken(StringBuilder source, StringBuilder dest)
            {
                if (source.Length == 0)
                {
                    return;
                }

                string segment = source.ToString();
                int lastDot = segment.LastIndexOf(value: '.');
                dest.Append(lastDot >= 0 ? segment[(lastDot + 1)..] : segment);
                source.Clear();
            }

            foreach (char ch in name)
            {
                if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or '/')
                {
                    token.Append(value: ch);
                    continue;
                }

                FlushToken(source: token, dest: sb);
                sb.Append(value: ch);
            }

            FlushToken(source: token, dest: sb);
            return sb.ToString();
        }

        static string GetAstTypeName(TypeExpression typeExpr)
        {
            if (typeExpr.GenericArguments is not { Count: > 0 })
            {
                return typeExpr.Name;
            }

            // `Routine[(params), ret]`: RoutineTypeInfo.Name renders the parameter-list tuple
            // PARENTHESIZED — `(T,)` for one element, `(A, B)` for several — and the return type
            // directly, NOT as `Tuple[...]` (see RoutineTypeInfo.BuildName). The AST instead parses
            // the param-list as a generic `Tuple[...]`. Render the Routine form to match exactly, so
            // lambda-taking protocol-extension methods (Iterable[T].where/select/accumulate/...) match
            // their registered RoutineInfo signature; otherwise their bodies aren't collected and
            // ProtocolDefaultImplLoweringPass can't synthesize per-implementer instances → "undefined
            // symbol" at codegen. Scoped to the Routine param-list ONLY — a standalone `Tuple[...]`
            // parameter keeps its `Tuple[...]` rendering (which matches TupleTypeInfo.Name).
            if (typeExpr.Name == "Routine" && typeExpr.GenericArguments.Count == 2)
            {
                TypeExpression paramTupleExpr = typeExpr.GenericArguments[index: 0];
                string paramList;
                if (paramTupleExpr.Name == "Tuple"
                    && paramTupleExpr.GenericArguments is { Count: > 0 } tupleArgs)
                {
                    paramList = tupleArgs.Count == 1
                        ? "(" + GetAstTypeName(typeExpr: tupleArgs[index: 0]) + ",)"
                        : "(" + string.Join(separator: ", ",
                            values: tupleArgs.Select(GetAstTypeName)) + ")";
                }
                else
                {
                    // 0-parameter routine type: RoutineTypeInfo.BuildName renders "None".
                    paramList = GetAstTypeName(typeExpr: paramTupleExpr);
                }

                return $"Routine[{paramList}, {GetAstTypeName(typeExpr: typeExpr.GenericArguments[index: 1])}]";
            }

            return $"{typeExpr.Name}[{string.Join(separator: ",",
                values: typeExpr.GenericArguments.Select(GetAstTypeName))}]";
        }

        var astParamTypeNames = new List<string>(capacity: decl.Parameters.Count);
        foreach (Parameter param in decl.Parameters)
        {
            if (param.Type == null)
            {
                return null;
            }

            astParamTypeNames.Add(item: NormalizeTypeName(name: GetAstTypeName(typeExpr: param.Type)));
        }

        return candidates.FirstOrDefault(candidate =>
        {
            if (candidate.IsFailable != decl.IsFailable)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(moduleName) && candidate.Module != null && candidate.Module != moduleName)
            {
                return false;
            }

            if (candidate.Parameters.Count != astParamTypeNames.Count)
            {
                return false;
            }

            for (int i = 0; i < astParamTypeNames.Count; i++)
            {
                string candidateTypeName = NormalizeTypeName(name: candidate.Parameters[i].Type.Name);
                if (candidateTypeName != astParamTypeNames[i])
                {
                    return false;
                }
            }

            return true;
        });
    }

    #endregion
}
