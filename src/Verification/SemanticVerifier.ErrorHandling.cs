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

        foreach ((Program program, _, _) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || !decl.IsFailable || decl.Body == null)
                    continue;

                bool hasDirect = generator.BodyHasThrowOrAbsent(body: decl.Body);

                RoutineInfo? routineInfo = ResolveRoutineInfoForDeclaration(decl: decl);
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
    /// Collects failable stdlib routine bodies into <c>_routineBodies</c> without running
    /// full semantic analysis. Scans stdlib program ASTs for failable member routine declarations,
    /// looks up their <see cref="RoutineInfo"/> in the registry, and stores the bodies so that
    /// <see cref="ErrorHandlingVariantPass"/> can generate try_/check_/lookup_
    /// variants for stdlib iterators (e.g., ListEmitter[T].$next!).
    /// Called before RunPhase4GlobalDesugaring() so variants exist when for-loops are lowered.
    /// </summary>
    private void CollectStdlibBodiesForVariantGeneration()
    {
        foreach ((Program program, _, _) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || !decl.IsFailable || decl.Body == null)
                    continue;

                // Only member routines -> standalone routines don't need $next variants
                if (!decl.Name.Contains('.'))
                    continue;

                RoutineInfo? routineInfo = ResolveRoutineInfoForDeclaration(decl: decl);
                if (routineInfo == null) continue;

                if (!_routineBodies.ContainsKey(key: routineInfo.RegistryKey))
                    _routineBodies[key: routineInfo.RegistryKey] = decl.Body;
            }
        }
    }

    private RoutineInfo? ResolveRoutineInfoForDeclaration(RoutineDeclaration decl, string? moduleName = null)
    {
        if (decl.Name.Contains('.'))
        {
            int dotIdx = decl.Name.LastIndexOf('.');
            string ownerTypeName = decl.Name[..dotIdx];
            string methodName = decl.Name[(dotIdx + 1)..];

            string lookupName = ownerTypeName.Contains('[')
                ? ownerTypeName[..ownerTypeName.IndexOf('[')]
                : ownerTypeName;

            TypeSymbol? ownerType = _registry.LookupType(name: lookupName);
            if (ownerType == null)
            {
                return null;
            }

            var candidates = new List<RoutineInfo>();
            _registry.CollectMemberRoutineCandidates(type: ownerType, methodName: methodName,
                candidates: candidates);
            return MatchRoutineDeclaration(candidates: candidates, decl: decl,
                moduleName: ownerType.Module);
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
