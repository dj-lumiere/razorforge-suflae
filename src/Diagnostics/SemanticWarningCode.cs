namespace Compiler.Diagnostics;

/// <summary>
/// Semantic warning codes for RazorForge (RF-W prefix).
/// Covers non-fatal issues that may indicate problems but allow building.
///
/// Code ranges:
/// - RF-W001-RF-W049: Unused Code Warnings
/// - RF-W050-RF-W099: Unreachable Code Warnings
/// - RF-W100-RF-W149: Deprecated Feature Warnings
/// - RF-W150-RF-W199: Redundant Code Warnings
/// - RF-W200-RF-W249: Modification Warnings
/// - RF-W250-RF-W299: Pattern Matching Warnings
/// - RF-W300-RF-W349: Style Warnings
/// - RF-W350-RF-W399: Performance Warnings
/// - RF-W400-RF-W449: Internal/Debug Warnings
/// </summary>
public enum SemanticWarningCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // UNUSED CODE WARNINGS (RF-W001 - RF-W049)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Variable is declared but never used.</summary>
    UnusedVariable = 1,

    /// <summary>Parameter is declared but never used.</summary>
    UnusedParameter = 2,

    /// <summary>Private routine is never called.</summary>
    UnusedPrivateRoutine = 3,

    /// <summary>Private type is never created or referenced.</summary>
    UnusedPrivateType = 4,

    /// <summary>Import is declared but no symbols from it are used.</summary>
    UnusedImport = 5,

    /// <summary>Member variable is declared but never read.</summary>
    UnusedMemberVariable = 6,

    /// <summary>Routine call's return value is unused. Use 'discard' to explicitly ignore it.</summary>
    UnusedRoutineReturnValue = 7,

    // ═══════════════════════════════════════════════════════════════════════════
    // UNREACHABLE CODE WARNINGS (RF-W050 - RF-W099)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Code after return statement is unreachable.</summary>
    UnreachableCodeAfterReturn = 50,

    /// <summary>Code after throw statement is unreachable.</summary>
    UnreachableCodeAfterThrow = 51,

    /// <summary>Code after break statement is unreachable.</summary>
    UnreachableCodeAfterBreak = 52,

    /// <summary>Code after continue statement is unreachable.</summary>
    UnreachableCodeAfterContinue = 53,

    /// <summary>Code after absent statement is unreachable.</summary>
    UnreachableCodeAfterAbsent = 54,

    /// <summary>Code in else branch is unreachable due to always-true condition.</summary>
    UnreachableElseBranch = 55,

    /// <summary>Code in then branch is unreachable due to always-false condition.</summary>
    UnreachableThenBranch = 56,

    /// <summary>Loop body is unreachable due to always-false condition.</summary>
    UnreachableLoopBody = 57,

    // ═══════════════════════════════════════════════════════════════════════════
    // DEPRECATED FEATURE WARNINGS (RF-W100 - RF-W149)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Using a deprecated type.</summary>
    DeprecatedType = 100,

    /// <summary>Using a deprecated routine.</summary>
    DeprecatedRoutine = 101,

    /// <summary>Using a deprecated member variable.</summary>
    DeprecatedMemberVariable = 102,

    /// <summary>Using a deprecated syntax construct.</summary>
    DeprecatedSyntax = 103,

    // ═══════════════════════════════════════════════════════════════════════════
    // REDUNDANT CODE WARNINGS (RF-W150 - RF-W199)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Method is readonly, no ! token needed.</summary>
    UnnecessaryModificationToken = 150,

    /// <summary>Redundant type annotation matches inferred type.</summary>
    RedundantTypeAnnotation = 151,

    /// <summary>Condition is always true.</summary>
    ConditionAlwaysTrue = 152,

    /// <summary>Condition is always false.</summary>
    ConditionAlwaysFalse = 153,

    /// <summary>Comparison with self is always true/false.</summary>
    SelfComparison = 154,

    /// <summary>Expression result is unused.</summary>
    UnusedExpressionResult = 155,

    // ═══════════════════════════════════════════════════════════════════════════
    // MODIFICATION WARNINGS (RF-W200 - RF-W249)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Variable marked modifiable but never modified.</summary>
    UnnecessaryModifiable = 200,

    /// <summary>Method could be marked @readonly but is not.</summary>
    MethodCouldBeReadonly = 201,

    /// <summary>Calling .hijack() on @initonly record — the record is frozen after construction.</summary>
    HijackOnInitOnly = 210,

    /// <summary>Task[T] result is not awaited — potential fire-and-forget bug.</summary>
    UnusedTaskResult = 215,

    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN MATCHING WARNINGS (RF-W250 - RF-W299)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>When expression may not cover all cases.</summary>
    NonExhaustiveWhen = 250,

    /// <summary>Pattern case is unreachable due to previous patterns.</summary>
    UnreachablePattern = 251,

    /// <summary>Wildcard pattern makes subsequent patterns unreachable.</summary>
    WildcardNotLast = 252,

    /// <summary>Deeply nested conditional expression; consider using 'when' for readability.</summary>
    NestedConditionalExpression = 253,

    /// <summary>Failable routine (!) contains no throw or absent — will never crash.</summary>
    FailableRoutineNeverCrashes = 254,

    /// <summary>throw/absent in a non-failable routine — add ! suffix or remove the statement.</summary>
    ThrowAbsentInNonFailable = 255,

    /// <summary>Bare entity type in variant member — use an RC wrapper (Retained[T], etc.) instead.</summary>
    BareEntityInVariantMember = 256,

    /// <summary>Failable routine called in a non-failable context without error handling.</summary>
    UnhandledCrashableCall = 257,

    // ═══════════════════════════════════════════════════════════════════════════
    // STYLE WARNINGS (RF-W300 - RF-W349)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type name does not follow PascalCase convention.</summary>
    TypeNameNotPascalCase = 300,

    /// <summary>Variable name does not follow camelCase convention.</summary>
    VariableNameNotCamelCase = 301,

    /// <summary>Constant name does not follow UPPER_CASE convention.</summary>
    ConstantNameNotUpperCase = 302,

    // ═══════════════════════════════════════════════════════════════════════════
    // PERFORMANCE WARNINGS (RF-W350 - RF-W399)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Large struct being passed by value; consider using a reference.</summary>
    LargeValueCopy = 350,

    /// <summary>Allocation in hot loop; consider hoisting.</summary>
    AllocationInLoop = 351,

    /// <summary>Nested Data wrapping (Data(Data(x))) should be flattened.</summary>
    NestedDataWrapping = 360,

    // ═══════════════════════════════════════════════════════════════════════════
    // INTERNAL/DEBUG WARNINGS (RF-W400 - RF-W449)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Unknown statement type encountered during analysis.</summary>
    UnknownStatementType = 400,

    /// <summary>Unknown expression type encountered during analysis.</summary>
    UnknownExpressionType = 401,

    /// <summary>Unexpected declaration in statement context.</summary>
    UnexpectedDeclaration = 402
}
/// <summary>
/// Provides formatting helpers for <see cref="SemanticWarningCode"/>.
/// </summary>

public static class SemanticWarningCodeExtensions
{
    extension(SemanticWarningCode code)
    {
        /// <summary>
        /// Formats code as RF-Wnnn (e.g., RF-W001, RF-W100)
        /// </summary>
        public string ToCodeString()
        {
            return $"SW{(int)code:D3}";
        }
        /// <summary>
        /// Gets the warning category for grouping and documentation.
        /// </summary>
        public string GetCategory()
        {
            return (int)code switch
            {
                < 50 => "Unused Code",
                < 100 => "Unreachable Code",
                < 150 => "Deprecated Feature",
                < 200 => "Redundant Code",
                < 250 => "Modification",
                < 300 => "Pattern Matching",
                < 350 => "Style",
                < 400 => "Performance",
                _ => "Internal"
            };
        }
        /// <summary>
        /// Gets whether this warning is enabled by default.
        /// </summary>
        public bool IsEnabledByDefault()
        {
            // Most warnings are enabled by default
            return (int)code switch
            {
                // Style warnings are not enabled by default
                >= 300 and < 350 => false,
                // Performance warnings are not enabled by default
                >= 350 and < 400 => false,
                // All others are enabled
                _ => true
            };
        }
    }
}
