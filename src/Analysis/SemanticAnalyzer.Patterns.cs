namespace Compilers.Analysis;

using Enums;
using Scopes;
using Types;
using Shared.AST;
using Shared.Lexer;
using global::RazorForge.Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Pattern analysis for when/is expressions.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Pattern Analysis

    /// <summary>
    /// Declares a pattern binding variable, checking for shadowing of variables in outer scopes.
    /// </summary>
    private void DeclarePatternVariable(string name, TypeSymbol type, SourceLocation location,
        bool isMutable = false)
    {
        // Check if this name exists in a parent scope (shadowing)
        Scope? parent = _registry.CurrentScope.Parent;
        if (parent?.LookupVariable(name: name) != null)
        {
            ReportError(
                SemanticDiagnosticCode.IdentifierShadowing,
                $"Pattern variable '{name}' shadows an existing variable in an outer scope.",
                location);
        }

        // Still declare the variable even if shadowing, to avoid cascading errors
        _registry.DeclareVariable(
            name: name,
            type: type,
            isMutable: isMutable);
    }

    private void AnalyzePattern(Pattern pattern, TypeSymbol matchedType)
    {
        switch (pattern)
        {
            case LiteralPattern:
                // Literal patterns don't bind variables
                // TODO: REALLY?
                break;

            case IdentifierPattern id:
                // Bind the matched value to the identifier
                DeclarePatternVariable(
                    name: id.Name,
                    type: matchedType,
                    location: id.Location);
                break;

            case TypePattern typePat:
                // None is a keyword, not a registered type — handle it directly
                if (typePat.Type.Name == "None")
                {
                    if (matchedType is not ErrorTypeInfo
                        && matchedType.Category != TypeCategory.ErrorHandling)
                    {
                        ReportError(
                            SemanticDiagnosticCode.PatternTypeMismatch,
                            $"Type pattern 'is None' can never match a value of type '{matchedType.Name}'.",
                            typePat.Location);
                    }

                    break;
                }

                TypeSymbol patternType = ResolveType(typeExpr: typePat.Type);

                // Check type compatibility between matched type and pattern type
                if (patternType is not ErrorTypeInfo
                    && matchedType is not ErrorTypeInfo
                    && !IsTypePatternCompatible(matchedType: matchedType, patternType: patternType))
                {
                    ReportError(
                        SemanticDiagnosticCode.PatternTypeMismatch,
                        $"Type pattern 'is {patternType.Name}' can never match a value of type '{matchedType.Name}'.",
                        typePat.Location);
                }

                if (typePat.VariableName != null)
                {
                    DeclarePatternVariable(
                        name: typePat.VariableName,
                        type: patternType,
                        location: typePat.Location);
                }

                // Process destructuring bindings if present
                if (typePat.Bindings is { Count: > 0 })
                {
                    foreach (DestructuringBinding binding in typePat.Bindings)
                    {
                        TypeSymbol fieldType = LookupFieldType(type: patternType, fieldName: binding.FieldName);

                        if (binding.NestedPattern != null)
                        {
                            AnalyzePattern(pattern: binding.NestedPattern, matchedType: fieldType);
                        }
                        else if (binding.BindingName != null)
                        {
                            DeclarePatternVariable(
                                name: binding.BindingName,
                                type: fieldType,
                                location: binding.Location);
                        }
                    }
                }

                break;

            case WildcardPattern:
                // Wildcards don't bind variables
                break;

            case VariantPattern variant:
                // Handle variant case matching - look up case from matched type
                AnalyzeVariantPattern(pattern: variant, matchedType: matchedType);
                break;

            case GuardPattern guard:
                // First analyze the inner pattern
                AnalyzePattern(pattern: guard.InnerPattern, matchedType: matchedType);
                // Then analyze the guard expression (must be bool)
                TypeSymbol guardType = AnalyzeExpression(expression: guard.Guard);
                if (!IsBoolType(type: guardType))
                {
                    ReportError(
                        SemanticDiagnosticCode.PatternGuardNotBool,
                        "Guard expression must be boolean.",
                        guard.Guard.Location);
                }

                break;

            case ElsePattern elsePat:
                if (elsePat.VariableName != null)
                {
                    DeclarePatternVariable(
                        name: elsePat.VariableName,
                        type: matchedType,
                        location: elsePat.Location);
                }

                break;

            case NonePattern:
            case CrashablePattern:
                // These don't bind variables directly
                break;

            case DestructuringPattern destruct:
                AnalyzeDestructuringPattern(pattern: destruct, sourceType: matchedType, isMutable: false);
                break;

            case TypeDestructuringPattern typeDestruct:
                AnalyzeTypeDestructuringPattern(pattern: typeDestruct);
                break;

            case ExpressionPattern exprPat:
                TypeSymbol exprType = AnalyzeExpression(expression: exprPat.Expression);
                if (!IsBoolType(type: exprType))
                {
                    ReportError(
                        SemanticDiagnosticCode.ExpressionPatternNotBool,
                        "Expression pattern must be boolean.",
                        exprPat.Location);
                }

                break;
        }
    }

    private void AnalyzeDestructuringPattern(DestructuringPattern pattern, TypeSymbol sourceType, bool isMutable)
    {
        foreach (DestructuringBinding binding in pattern.Bindings)
        {
            TypeSymbol fieldType = ErrorTypeInfo.Instance;

            // Get field type from source type
            if (binding.FieldName != null && sourceType is RecordTypeInfo record)
            {
                fieldType = record.LookupField(fieldName: binding.FieldName)?.Type ?? ErrorTypeInfo.Instance;
            }
            else if (binding.FieldName != null && sourceType is EntityTypeInfo entity)
            {
                fieldType = entity.LookupField(fieldName: binding.FieldName)?.Type ?? ErrorTypeInfo.Instance;
            }

            if (binding.NestedPattern != null)
            {
                // Handle nested destructuring
                AnalyzePattern(pattern: binding.NestedPattern, matchedType: fieldType);
            }
            else if (binding.BindingName != null)
            {
                DeclarePatternVariable(
                    name: binding.BindingName,
                    type: fieldType,
                    location: binding.Location,
                    isMutable: isMutable);
            }
        }
    }

    /// <summary>
    /// Analyzes a variant pattern, looking up the case type from the matched variant/mutant type.
    /// </summary>
    /// <param name="pattern">The variant pattern to analyze.</param>
    /// <param name="matchedType">The type being matched against.</param>
    private void AnalyzeVariantPattern(VariantPattern pattern, TypeSymbol matchedType)
    {
        // Get the cases from the matched type
        IReadOnlyList<VariantCaseInfo>? cases = matchedType switch
        {
            VariantTypeInfo variant => variant.Cases,
            MutantTypeInfo mutant => mutant.Cases,
            _ => null
        };

        if (cases == null)
        {
            ReportError(
                SemanticDiagnosticCode.VariantPatternOnNonVariant,
                $"Cannot match variant pattern against non-variant type '{matchedType.Name}'.",
                pattern.Location);
            // Still declare bindings with error type to avoid cascading errors
            DeclareBindingsWithErrorType(bindings: pattern.Bindings);
            return;
        }

        // Find the matching case
        VariantCaseInfo? matchedCase = cases.FirstOrDefault(c => c.Name == pattern.CaseName);
        if (matchedCase == null)
        {
            ReportError(
                SemanticDiagnosticCode.VariantCaseNotFound,
                $"Variant type '{matchedType.Name}' does not have a case named '{pattern.CaseName}'.",
                pattern.Location);
            DeclareBindingsWithErrorType(bindings: pattern.Bindings);
            return;
        }

        // Bind the payload if present
        if (pattern.Bindings is not { Count: > 0 })
        {
            return;
        }

        if (!matchedCase.HasPayload)
        {
            ReportError(
                SemanticDiagnosticCode.VariantCaseNoPayload,
                $"Variant case '{pattern.CaseName}' has no payload to destructure.",
                pattern.Location);
            return;
        }

        // Variant cases have a single payload type
        TypeSymbol payloadType = matchedCase.PayloadType!;

        // For a single binding without field name, bind directly to the payload
        if (pattern.Bindings.Count == 1 && pattern.Bindings[0].FieldName == null)
        {
            DestructuringBinding binding = pattern.Bindings[0];
            if (binding.NestedPattern != null)
            {
                AnalyzePattern(pattern: binding.NestedPattern, matchedType: payloadType);
            }
            else if (binding.BindingName != null)
            {
                DeclarePatternVariable(
                    name: binding.BindingName,
                    type: payloadType,
                    location: binding.Location);
            }
        }
        else
        {
            // Multiple bindings - payload must be a record/entity type
            foreach (DestructuringBinding binding in pattern.Bindings)
            {
                TypeSymbol fieldType = LookupFieldType(type: payloadType, fieldName: binding.FieldName);

                if (binding.NestedPattern != null)
                {
                    AnalyzePattern(pattern: binding.NestedPattern, matchedType: fieldType);
                }
                else if (binding.BindingName != null)
                {
                    DeclarePatternVariable(
                        name: binding.BindingName,
                        type: fieldType,
                        location: binding.Location);
                }
            }
        }
    }

    /// <summary>
    /// Analyzes a type destructuring pattern, looking up field types from the target type.
    /// </summary>
    /// <param name="pattern">The type destructuring pattern to analyze.</param>
    private void AnalyzeTypeDestructuringPattern(TypeDestructuringPattern pattern)
    {
        TypeSymbol targetType = ResolveType(typeExpr: pattern.Type);

        foreach (DestructuringBinding binding in pattern.Bindings)
        {
            TypeSymbol fieldType = LookupFieldType(type: targetType, fieldName: binding.FieldName);

            if (binding.NestedPattern != null)
            {
                AnalyzePattern(pattern: binding.NestedPattern, matchedType: fieldType);
            }
            else if (binding.BindingName != null)
            {
                DeclarePatternVariable(
                    name: binding.BindingName,
                    type: fieldType,
                    location: binding.Location);
            }
        }
    }

    /// <summary>
    /// Looks up a field type from a type. Returns ErrorTypeInfo if not found.
    /// </summary>
    private TypeSymbol LookupFieldType(TypeSymbol type, string? fieldName)
    {
        if (fieldName == null)
        {
            return ErrorTypeInfo.Instance;
        }

        return type switch
        {
            RecordTypeInfo record => record.LookupField(fieldName: fieldName)?.Type ?? ErrorTypeInfo.Instance,
            EntityTypeInfo entity => entity.LookupField(fieldName: fieldName)?.Type ?? ErrorTypeInfo.Instance,
            ResidentTypeInfo resident => resident.LookupField(fieldName: fieldName)?.Type ?? ErrorTypeInfo.Instance,
            _ => ErrorTypeInfo.Instance
        };
    }

    /// <summary>
    /// Declares bindings with error type to prevent cascading errors.
    /// </summary>
    private void DeclareBindingsWithErrorType(List<DestructuringBinding>? bindings)
    {
        if (bindings == null)
        {
            return;
        }

        foreach (DestructuringBinding binding in bindings)
        {
            if (binding.BindingName != null)
            {
                _registry.DeclareVariable(
                    name: binding.BindingName,
                    type: ErrorTypeInfo.Instance,
                    isMutable: false);
            }
        }
    }

    /// <summary>
    /// Checks if a type pattern can potentially match a value of the given matched type.
    /// Returns true if the match is possible, false if provably impossible.
    /// </summary>
    private bool IsTypePatternCompatible(TypeSymbol matchedType, TypeSymbol patternType)
    {
        // Same type - always compatible
        if (matchedType.Name == patternType.Name)
        {
            return true;
        }

        // If either is a type parameter, we can't know at analysis time
        if (matchedType.Category == TypeCategory.TypeParameter
            || patternType.Category == TypeCategory.TypeParameter)
        {
            return true;
        }

        // If matched type is a protocol, any concrete type could conform
        if (matchedType.Category == TypeCategory.Protocol)
        {
            return true;
        }

        // If pattern type is a protocol, check if matched type implements it
        if (patternType.Category == TypeCategory.Protocol)
        {
            return ImplementsProtocol(type: matchedType, protocolName: patternType.Name);
        }

        // Error handling types (Maybe<T>, Result<T>, Lookup<T>) - allow matching inner types
        if (matchedType.Category == TypeCategory.ErrorHandling)
        {
            return true;
        }

        // IsAssignableTo in either direction covers subtyping
        if (IsAssignableTo(source: matchedType, target: patternType)
            || IsAssignableTo(source: patternType, target: matchedType))
        {
            return true;
        }

        // Provably incompatible
        return false;
    }

    #endregion

    #region Exhaustiveness Checking

    /// <summary>
    /// Result of exhaustiveness analysis.
    /// </summary>
    private readonly record struct ExhaustivenessResult(bool IsExhaustive, List<string> MissingCases);

    /// <summary>
    /// Checks whether the given when clauses exhaustively cover all cases of the matched type.
    /// </summary>
    private ExhaustivenessResult CheckExhaustiveness(
        IReadOnlyList<WhenClause> clauses,
        TypeSymbol matchedType)
    {
        // If any clause is a catch-all pattern, it's always exhaustive
        foreach (WhenClause clause in clauses)
        {
            if (clause.Pattern is WildcardPattern or ElsePattern or IdentifierPattern)
            {
                return new ExhaustivenessResult(IsExhaustive: true, MissingCases: []);
            }
        }

        return matchedType switch
        {
            ChoiceTypeInfo choice => CheckChoiceExhaustiveness(clauses: clauses, choice: choice),
            VariantTypeInfo variant => CheckVariantExhaustiveness(clauses: clauses, cases: variant.Cases,
                typeName: variant.Name),
            MutantTypeInfo mutant => CheckVariantExhaustiveness(clauses: clauses, cases: mutant.Cases,
                typeName: mutant.Name),
            ErrorHandlingTypeInfo eh => CheckErrorHandlingExhaustiveness(clauses: clauses, ehType: eh),
            _ when matchedType.Name == "Bool" => CheckBoolExhaustiveness(clauses: clauses),
            _ => new ExhaustivenessResult(IsExhaustive: false, MissingCases: [])
        };
    }

    /// <summary>
    /// Checks whether all cases of a choice type are covered by == ComparisonPatterns.
    /// </summary>
    private static ExhaustivenessResult CheckChoiceExhaustiveness(
        IReadOnlyList<WhenClause> clauses,
        ChoiceTypeInfo choice)
    {
        var coveredCases = new HashSet<string>();

        foreach (WhenClause clause in clauses)
        {
            string? caseName = ExtractChoiceCaseName(pattern: clause.Pattern);
            if (caseName != null)
            {
                coveredCases.Add(item: caseName);
            }
        }

        var missingCases = choice.Cases
            .Where(predicate: c => !coveredCases.Contains(c.Name))
            .Select(selector: c => c.Name)
            .ToList();

        return new ExhaustivenessResult(
            IsExhaustive: missingCases.Count == 0,
            MissingCases: missingCases);
    }

    /// <summary>
    /// Extracts the choice case name from a ComparisonPattern with == operator.
    /// Returns null if the pattern is not a choice case comparison.
    /// </summary>
    private static string? ExtractChoiceCaseName(Pattern pattern)
    {
        if (pattern is not ComparisonPattern { Operator: TokenType.Equal } cmp)
        {
            return null;
        }

        return cmp.Value switch
        {
            // Status.ACTIVE → PropertyName is the case name
            MemberExpression member => member.PropertyName,
            // ACTIVE (shorthand) → Name is the case name
            IdentifierExpression id => id.Name,
            _ => null
        };
    }

    /// <summary>
    /// Checks whether all cases of a variant/mutant type are covered.
    /// The parser creates TypePattern for variant case matching (is Shape.CIRCLE or is CIRCLE),
    /// not VariantPattern (which is defined in the AST but never instantiated by the parser).
    /// </summary>
    private static ExhaustivenessResult CheckVariantExhaustiveness(
        IReadOnlyList<WhenClause> clauses,
        IReadOnlyList<VariantCaseInfo> cases,
        string typeName)
    {
        var coveredCases = new HashSet<string>();

        foreach (WhenClause clause in clauses)
        {
            string? caseName = ExtractVariantCaseName(pattern: clause.Pattern, typeName: typeName);
            if (caseName != null)
            {
                coveredCases.Add(item: caseName);
            }
        }

        var missingCases = cases
            .Where(predicate: c => !coveredCases.Contains(c.Name))
            .Select(selector: c => c.Name)
            .ToList();

        return new ExhaustivenessResult(
            IsExhaustive: missingCases.Count == 0,
            MissingCases: missingCases);
    }

    /// <summary>
    /// Extracts the variant case name from a pattern.
    /// Handles TypePattern with dotted names (is Shape.CIRCLE) or bare names (is CIRCLE),
    /// as well as VariantPattern (if ever instantiated).
    /// </summary>
    private static string? ExtractVariantCaseName(Pattern pattern, string typeName)
    {
        switch (pattern)
        {
            case TypePattern typePat:
            {
                string name = typePat.Type.Name;

                // Dotted form: "Shape.CIRCLE" → extract "CIRCLE"
                if (name.StartsWith(value: typeName + ".", comparisonType: StringComparison.Ordinal))
                {
                    return name[(typeName.Length + 1)..];
                }

                // Bare form: "CIRCLE" — matches if it's a known case name
                // (caller will validate against the case list)
                if (!name.Contains(value: '.'))
                {
                    return name;
                }

                return null;
            }
            case VariantPattern variant:
                return variant.CaseName;
            default:
                return null;
        }
    }

    /// <summary>
    /// Checks whether Maybe/Result/Lookup error handling types are exhaustively matched.
    /// </summary>
    private ExhaustivenessResult CheckErrorHandlingExhaustiveness(
        IReadOnlyList<WhenClause> clauses,
        ErrorHandlingTypeInfo ehType)
    {
        bool hasNone = false;
        bool hasCrashable = false;
        bool hasValue = false;

        foreach (WhenClause clause in clauses)
        {
            if (IsNonePattern(pattern: clause.Pattern))
            {
                hasNone = true;
            }
            else if (IsCrashablePattern(pattern: clause.Pattern))
            {
                hasCrashable = true;
            }
            else if (clause.Pattern is not (NonePattern or CrashablePattern))
            {
                // Any other pattern (type check, literal, etc.) counts as value arm
                hasValue = true;
            }
        }

        var missing = new List<string>();

        switch (ehType.Kind)
        {
            case ErrorHandlingKind.Maybe:
                if (!hasNone) missing.Add(item: "None");
                if (!hasValue) missing.Add(item: "value");
                break;
            case ErrorHandlingKind.Result:
                if (!hasCrashable) missing.Add(item: "Crashable");
                if (!hasValue) missing.Add(item: "value");
                break;
            case ErrorHandlingKind.Lookup:
                if (!hasNone) missing.Add(item: "None");
                if (!hasCrashable) missing.Add(item: "Crashable");
                if (!hasValue) missing.Add(item: "value");
                break;
        }

        return new ExhaustivenessResult(
            IsExhaustive: missing.Count == 0,
            MissingCases: missing);
    }

    /// <summary>
    /// Checks whether both true and false are covered by literal patterns.
    /// </summary>
    private static ExhaustivenessResult CheckBoolExhaustiveness(IReadOnlyList<WhenClause> clauses)
    {
        bool hasTrue = false;
        bool hasFalse = false;

        foreach (WhenClause clause in clauses)
        {
            if (clause.Pattern is LiteralPattern { LiteralType: TokenType.True })
            {
                hasTrue = true;
            }
            else if (clause.Pattern is LiteralPattern { LiteralType: TokenType.False })
            {
                hasFalse = true;
            }
        }

        var missing = new List<string>();
        if (!hasTrue) missing.Add(item: "true");
        if (!hasFalse) missing.Add(item: "false");

        return new ExhaustivenessResult(
            IsExhaustive: missing.Count == 0,
            MissingCases: missing);
    }

    #endregion
}
