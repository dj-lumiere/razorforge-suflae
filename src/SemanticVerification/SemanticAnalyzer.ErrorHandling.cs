namespace SemanticVerification;

using Compiler.Synthesis;
using TypeModel.Symbols;
using SyntaxTree;
using TypeSymbol = TypeModel.Types.TypeInfo;

/// <summary>
/// Phase 5: Error handling variant support.
///
/// RazorForge/Suflae error handling model:
/// - Failable functions end with ! suffix (e.g., parse!, connect!)
/// - throw statement: signals a failure with an error value
/// - absent statement: signals "not found" without error
///
/// Variant generation rules:
/// - Only absent: try_ (returns T? ??None on absent)
/// - Only throw: try_ (returns T? ??None on throw) + check_ (returns Result&lt;T&gt;)
/// - Both throw and absent: try_ + lookup_ (returns Lookup&lt;T&gt;)
///
/// The actual variant generation is delegated to <see cref="Synthesis.ErrorHandlingVariantPass"/>
/// which runs in Phase 4 (global desugaring) after body analysis populates <c>_routineBodies</c>.
/// </summary>
public sealed partial class SemanticAnalyzer
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
    /// Uses AST-level throw/absent detection ??no full semantic analysis required.
    /// </summary>
    internal void PreRegisterUserVariants(Program program)
    {
        var generator = new ErrorHandlingGenerator(registry: _registry);

        foreach (IAstNode node in program.Declarations)
        {
            if (node is not RoutineDeclaration routineDecl || !routineDecl.IsFailable ||
                routineDecl.Body == null)
            {
                continue;
            }

            // Quick AST scan ??skip routines with no throw/absent nodes
            if (!generator.BodyHasThrowOrAbsent(body: routineDecl.Body))
            {
                continue;
            }

            // Build the lookup name (module-qualified for standalone routines)
            string? mod = GetCurrentModuleName();
            string fullName = string.IsNullOrEmpty(value: mod)
                ? routineDecl.Name
                : $"{mod}.{routineDecl.Name}";

            RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: routineDecl.Name)
                ?? _registry.LookupRoutine(fullName: fullName);

            if (routineInfo == null || !routineInfo.IsFailable) continue;
            if (routineInfo.Annotations.Any(predicate: a => a == "crash_only")) continue;

            // Generate and register variants from the parsed body (AST scan, no SA errors)
            ErrorHandlingResult result =
                generator.GenerateVariants(routine: routineInfo, body: routineDecl.Body);
            if (result.Error != null) continue;

            foreach (GeneratedVariant variant in result.Variants)
            {
                _registry.RegisterRoutine(routine: variant.Routine);
            }
        }
    }

    /// <summary>
    /// Collects failable stdlib routine bodies into <c>_routineBodies</c> without running
    /// full semantic analysis. Scans stdlib program ASTs for failable member routine declarations,
    /// looks up their <see cref="RoutineInfo"/> in the registry, and stores the bodies so that
    /// <see cref="Synthesis.ErrorHandlingVariantPass"/> can generate try_/check_/lookup_
    /// variants for stdlib iterators (e.g., ListEmitter[T].$next!).
    /// Called before RunPhase4GlobalDesugaring() so variants exist when for-loops are lowered.
    /// </summary>
    private void CollectStdlibBodiesForVariantGeneration()
    {
        foreach ((Program program, _, _) in _registry.StdlibPrograms)
        {
            foreach (IAstNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || !decl.IsFailable || decl.Body == null)
                    continue;

                // Only member routines ??standalone routines don't need $next variants
                if (!decl.Name.Contains('.'))
                    continue;

                int dotIdx = decl.Name.LastIndexOf('.');
                string ownerTypeName = decl.Name[..dotIdx];
                string methodName = decl.Name[(dotIdx + 1)..];

                // Strip generic params for type lookup (e.g., "ListEmitter[T]" ??"ListEmitter")
                string lookupName = ownerTypeName.Contains('[')
                    ? ownerTypeName[..ownerTypeName.IndexOf('[')]
                    : ownerTypeName;

                TypeSymbol? ownerType = _registry.LookupType(name: lookupName);
                if (ownerType == null) continue;

                RoutineInfo? routineInfo = _registry.LookupMethod(
                    type: ownerType, methodName: methodName, isFailable: true);
                if (routineInfo == null) continue;

                if (!_routineBodies.ContainsKey(key: routineInfo.RegistryKey))
                    _routineBodies[key: routineInfo.RegistryKey] = decl.Body;
            }
        }
    }

    #endregion
}
