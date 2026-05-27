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
/// which runs in Phase 4 (global desugaring) after body analysis populates <c>_routineBodies</c>.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 5: Error Handling Body Collection

    /// <summary>
    /// Storage for routine bodies needed during variant generation.
    /// Maps RoutineInfo.RegistryKey to its body statement.
    /// Populated during body analysis; consumed by ErrorHandlingVariantPass in Phase 4.
    /// </summary>
    private readonly Dictionary<string, Statement> _routineBodies = new();

    /// <summary>
    /// Stores a routine body for later variant generation.
    /// Called during Phase 5 body analysis.
    /// </summary>
    /// <param name="routine">The routine whose body is being stored.</param>
    /// <param name="body">The routine's body statement.</param>
    private void StoreRoutineBody(RoutineInfo routine, Statement body)
    {
        _routineBodies[key: routine.RegistryKey] = body;
    }

    /// <summary>
    /// Phase 2.8: Pre-register error handling variant stubs for user-defined failable routines.
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
                _registry.RegisterRoutine(routine: variant.Routine);
            }
        }
    }

    /// <summary>
    /// Phase 3 global: pre-registers try_/check_/lookup_ stub variants for all failable stdlib
    /// member routines (e.g., Tracked[T].recover!, ListEmitter[T].$next!).
    /// Must run before Phase 5 user-body analysis so that user code calling these variants
    /// (e.g., <c>rt.try_recover()</c> or desugared for-loop <c>iter.try_next()</c>) resolves
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
    /// <c>ListEmitter[T].$next!</c>) and <see cref="Compiler.Instantiation.Passes.ProtocolDefaultImplLoweringPass"/>
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
            string bareLookupName = ownerTypeName.Contains('[')
                ? ownerTypeName[..ownerTypeName.IndexOf('[')]
                : ownerTypeName;

            TypeSymbol? bareOwner = _registry.LookupType(name: bareLookupName);
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
