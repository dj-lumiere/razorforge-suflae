namespace Compilers.Analysis;

using Enums;
using Scopes;
using Types;
using Shared.AST;
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
}
