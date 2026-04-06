namespace SemanticAnalysis;

using Inference;
using Symbols;
using SyntaxTree;

/// <summary>
/// Phase 5: Error handling variant generation.
///
/// RazorForge/Suflae error handling model:
/// - Failable functions end with ! suffix (e.g., parse!, connect!)
/// - throw statement: signals a failure with an error value
/// - absent statement: signals "not found" without error
///
/// Variant generation rules:
/// - Only absent: try_ (returns T? → None on absent)
/// - Only throw: try_ (returns T? → None on throw) + check_ (returns Result&lt;T&gt;)
/// - Both throw and absent: try_ + lookup_ (returns Lookup&lt;T&gt;)
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Phase 5: Error Handling Variant Generation

    /// <summary>
    /// Error handling generator instance.
    /// </summary>
    private ErrorHandlingGenerator? _errorHandlingGenerator;

    /// <summary>
    /// Storage for routine bodies needed during variant generation.
    /// Maps routine full name to its body statement.
    /// </summary>
    private readonly Dictionary<string, Statement> _routineBodies = new();

    /// <summary>
    /// Generates error handling variants for failable routines.
    /// Called after Phase 3 body analysis is complete.
    /// </summary>
    private void GenerateErrorHandlingVariants()
    {
        _errorHandlingGenerator = new ErrorHandlingGenerator(registry: _registry);

        // Create a snapshot of routines before iterating, since we'll be adding variants
        var routines = _registry.GetAllRoutines()
                                .ToList();

        // Find all failable routines and generate variants
        foreach (RoutineInfo routine in routines)
        {
            if (!routine.IsFailable)
            {
                continue;
            }

            // Get the routine's body (stored during body analysis)
            if (!_routineBodies.TryGetValue(key: routine.RegistryKey, value: out Statement? body))
            {
                // No body found - likely an imported or abstract routine
                continue;
            }

            GenerateVariantsForRoutine(routine: routine, body: body);
        }

        // Also generate try_ aliases for non-failable emitting routines so that EmitFor can always
        // call try_next uniformly. Failable emitters already have try_next from the loop above.
        foreach (RoutineInfo routine in routines)
        {
            RoutineInfo? alias = _errorHandlingGenerator!.GenerateTryAlias(original: routine);
            if (alias != null)
            {
                _registry.RegisterRoutine(routine: alias);
            }
        }

    }

    /// <summary>
    /// Generates error handling variants for a single failable routine.
    /// </summary>
    /// <param name="routine">The failable routine.</param>
    /// <param name="body">The routine's body statement.</param>
    private void GenerateVariantsForRoutine(RoutineInfo routine, Statement body)
    {
        // @crash_only: still analyze throw/absent but don't generate safe variants (#76)
        if (routine.Annotations.Any(predicate: a => a == "crash_only"))
        {
            ErrorHandlingResult crashOnlyResult =
                _errorHandlingGenerator!.GenerateVariants(routine: routine, body: body);
            routine.HasThrow = crashOnlyResult.HasThrow;
            routine.HasAbsent = crashOnlyResult.HasAbsent;
            return;
        }

        ErrorHandlingResult result =
            _errorHandlingGenerator!.GenerateVariants(routine: routine, body: body);

        // If variant generation fails (e.g., @llvm_ir routines with no throw/absent AST nodes),
        // just skip — no error reported. These are external implementations that don't need
        // generated safe variants.
        if (result.Error != null)
        {
            return;
        }

        // Register generated variants in the type registry
        foreach (GeneratedVariant variant in result.Variants)
        {
            _registry.RegisterRoutine(routine: variant.Routine);

            // Update routine flags from analysis
            routine.HasThrow = result.HasThrow;
            routine.HasAbsent = result.HasAbsent;
        }
    }

    /// <summary>
    /// Stores a routine body for later variant generation.
    /// Called during Phase 3 body analysis.
    /// </summary>
    /// <param name="routine">The routine whose body is being stored.</param>
    /// <param name="body">The routine's body statement.</param>
    private void StoreRoutineBody(RoutineInfo routine, Statement body)
    {
        _routineBodies[key: routine.RegistryKey] = body;
    }

    #endregion
}
