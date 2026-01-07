namespace Compilers.Analysis;

using Compilers.Analysis.Inference;
using Compilers.Analysis.Symbols;
using Compilers.Shared.AST;

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
        List<RoutineInfo> routines = _registry.GetAllRoutines().ToList();

        // Find all failable routines and generate variants
        foreach (RoutineInfo routine in routines)
        {
            if (!routine.IsFailable)
            {
                continue;
            }

            // Get the routine's body (stored during body analysis)
            if (!_routineBodies.TryGetValue(key: routine.FullName, value: out Statement? body))
            {
                // No body found - likely an imported or abstract routine
                continue;
            }

            GenerateVariantsForRoutine(routine: routine, body: body);
        }
    }

    /// <summary>
    /// Generates error handling variants for a single failable routine.
    /// </summary>
    /// <param name="routine">The failable routine.</param>
    /// <param name="body">The routine's body statement.</param>
    private void GenerateVariantsForRoutine(RoutineInfo routine, Statement body)
    {
        ErrorHandlingResult result = _errorHandlingGenerator!.GenerateVariants(
            routine: routine,
            body: body);

        // Report any errors from variant generation
        if (result.Error != null)
        {
            ReportError(
                message: result.Error,
                location: routine.Location ?? new SourceLocation(
                    FileName: "<unknown>",
                    Line: 0,
                    Column: 0,
                    Position: 0));
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
        _routineBodies[key: routine.FullName] = body;
    }

    #endregion
}
