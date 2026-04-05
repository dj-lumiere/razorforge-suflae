namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private record NarrowingInfo(
        string VariableName,
        TypeSymbol? ThenBranchType,
        TypeSymbol? ElseBranchType);

    /// <summary>
    /// Attempts to extract type narrowing information from a condition expression.
    /// Handles patterns like "x is None", "x isnot None", "Not(x is None)".
    /// </summary>
    private NarrowingInfo? TryExtractNarrowingFromCondition(Expression condition)
    {
        // Handle: x is None / x is Crashable / x isnot None / x isnot Crashable
        if (condition is IsPatternExpression isPat)
        {
            return ExtractFromIsPattern(isPat: isPat);
        }

        // Handle desugared unless: Not(x is None) → if Not(condition) { ... }
        if (condition is UnaryExpression
            {
                Operator: UnaryOperator.Not, Operand: IsPatternExpression innerIsPat
            })
        {
            // Negating the condition swaps then/else narrowing
            NarrowingInfo? inner = ExtractFromIsPattern(isPat: innerIsPat);
            if (inner == null)
            {
                return null;
            }

            return new NarrowingInfo(VariableName: inner.VariableName,
                ThenBranchType: inner.ElseBranchType,
                ElseBranchType: inner.ThenBranchType);
        }

        return null;
    }

    /// <summary>
    /// Extracts narrowing info from an IsPatternExpression.
    /// </summary>
    private NarrowingInfo? ExtractFromIsPattern(IsPatternExpression isPat)
    {
        // The expression must be a simple identifier
        if (isPat.Expression is not IdentifierExpression id)
        {
            return null;
        }

        // Look up the variable to get its current type
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo == null)
        {
            return null;
        }

        // Check for existing narrowing
        TypeSymbol varType = _registry.GetNarrowedType(name: id.Name) ?? varInfo.Type;

        bool eliminateNone = IsNonePattern(pattern: isPat.Pattern);
        bool eliminateCrashable = IsCrashablePattern(pattern: isPat.Pattern);

        if (!eliminateNone && !eliminateCrashable)
        {
            return null;
        }

        TypeSymbol? narrowedType = ComputeNarrowedType(type: varType,
            eliminateNone: eliminateNone,
            eliminateCrashable: eliminateCrashable);

        if (narrowedType == null)
        {
            return null;
        }

        if (isPat.IsNegated)
        {
            // "x isnot None" → then branch gets the narrowed type
            return new NarrowingInfo(VariableName: id.Name,
                ThenBranchType: narrowedType,
                ElseBranchType: null);
        }

        // "x is None" → else branch gets the narrowed type
        return new NarrowingInfo(VariableName: id.Name,
            ThenBranchType: null,
            ElseBranchType: narrowedType);
    }

    /// <summary>
    /// Checks if a pattern represents a None check.
    /// The parser creates TypePattern(type: "None") rather than NonePattern.
    /// </summary>
    private static bool IsNonePattern(Pattern pattern)
    {
        return pattern is NonePattern or TypePattern { Type.Name: "None" };
    }

    /// <summary>
    /// Checks if a pattern represents a Crashable check.
    /// The parser creates TypePattern(type: "Crashable") rather than CrashablePattern.
    /// </summary>
    private static bool IsCrashablePattern(Pattern pattern)
    {
        return pattern is CrashablePattern or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Checks if a pattern is a generic Crashable catch-all (not a specific error type).
    /// 'is Crashable e' is a catch-all; 'is FileNotFoundError e' is not.
    /// </summary>
    private static bool IsCrashableCatchAll(Pattern pattern)
    {
        return pattern is CrashablePattern { ErrorType: null }
            or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Computes the narrowed type after eliminating None and/or Crashable possibilities.
    /// </summary>
    /// <returns>The narrowed type, or null if narrowing is not possible.</returns>
    private static TypeSymbol? ComputeNarrowedType(TypeSymbol type, bool eliminateNone,
        bool eliminateCrashable)
    {
        string? baseName = GetCarrierBaseName(type: type);
        if (baseName == null || type.TypeArguments is not { Count: > 0 })
        {
            return null;
        }

        TypeSymbol valueType = type.TypeArguments[index: 0];

        return baseName switch
        {
            // Maybe<T>: eliminate None → T
            "Maybe" when eliminateNone => valueType,

            // Result<T>: eliminate Crashable → T
            "Result" when eliminateCrashable => valueType,

            // Lookup<T>: must eliminate both None and Crashable → T
            "Lookup" when eliminateNone && eliminateCrashable => valueType,

            // Partial elimination on Lookup is not sufficient
            _ => null
        };
    }

    /// <summary>
    /// Checks if a statement always produces a return value (return, throw, absent, becomes).
    /// Used for missing-return validation (#144).
    /// Unlike <see cref="HasDefiniteExit"/>, this does not count break/continue as terminating,
    /// since they exit loops but don't return a value from the routine.
    /// </summary>
    private static bool StatementAlwaysTerminates(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(predicate: s =>
                                        StatementAlwaysTerminates(statement: s)),
            IfStatement { ElseStatement: not null } ifStmt =>
                StatementAlwaysTerminates(statement: ifStmt.ThenStatement) &&
                StatementAlwaysTerminates(statement: ifStmt.ElseStatement),
            WhenStatement whenStmt => whenStmt.Clauses.Count > 0 &&
                                      whenStmt.Clauses.Any(predicate: c =>
                                          c.Pattern is ElsePattern or WildcardPattern) &&
                                      whenStmt.Clauses.All(predicate: c =>
                                          StatementAlwaysTerminates(statement: c.Body)),
            _ => false
        };
    }

    /// <summary>
    /// Checks if a statement always exits the current scope (return, throw, absent, break, continue).
    /// Used for guard clause narrowing.
    /// </summary>
    private static bool HasDefiniteExit(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BreakStatement => true,
            ContinueStatement => true,
            BlockStatement block => block.Statements.Any(predicate: s =>
                                        HasDefiniteExit(statement: s)),
            IfStatement { ElseStatement: not null } ifStmt =>
                HasDefiniteExit(statement: ifStmt.ThenStatement) &&
                HasDefiniteExit(statement: ifStmt.ElseStatement),
            _ => false
        };
    }

    /// <summary>
    /// Fixed-width numeric type names (excludes system-dependent SAddr/UAddr).
    /// </summary>
    private static readonly HashSet<string> FixedWidthNumericTypeNames =
    [
        "S8", "S16", "S32", "S64", "S128",
        "U8", "U16", "U32", "U64", "U128",
        "F16", "F32", "F64", "F128",
        "D32", "D64", "D128"
    ];

    /// <summary>
    /// Returns true if the type is a fixed-width numeric type (excludes SAddr/UAddr).
    /// </summary>
    private static bool IsFixedWidthNumericType(TypeInfo type)
    {
        return FixedWidthNumericTypeNames.Contains(item: type.Name);
    }
}
