namespace Compilers.Analysis;

using Enums;
using Types;
using Shared.AST;
using global::RazorForge.Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Type resolution for type expressions.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Type Resolution

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// Nullable types (T?) are desugared to Maybe&lt;T&gt; at parse time,
    /// so by the time we see them here, they're already Maybe&lt;T&gt;.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve.</param>
    /// <returns>The resolved type, or an error type if resolution fails.</returns>
    public TypeSymbol ResolveType(TypeExpression? typeExpr)
    {
        if (typeExpr == null)
        {
            return ErrorTypeInfo.Instance;
        }

        // Handle generic types (List<T>, Dict<K, V>, Maybe<T>)
        if (typeExpr.GenericArguments is { Count: > 0 })
        {
            return ResolveGenericType(typeExpr: typeExpr);
        }

        // Try to look up the type by name
        TypeSymbol? resolved = _registry.LookupType(name: typeExpr.Name);
        if (resolved != null)
        {
            return resolved;
        }

        // Check for generic type parameters in current context
        if (IsGenericParameter(name: typeExpr.Name))
        {
            return new GenericParameterTypeInfo(name: typeExpr.Name);
        }

        // Type not found
        ReportError(
            SemanticDiagnosticCode.UnknownType,
            $"Unknown type '{typeExpr.Name}'.",
            typeExpr.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Resolves a type expression within a protocol context.
    /// Handles the special 'Me' type which represents the implementing type.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve.</param>
    /// <returns>The resolved type, or ProtocolSelfTypeInfo for 'Me'.</returns>
    private TypeSymbol ResolveProtocolType(TypeExpression? typeExpr)
    {
        if (typeExpr == null)
        {
            return ErrorTypeInfo.Instance;
        }

        // Handle the special 'Me' type in protocol signatures
        if (typeExpr is { Name: "Me", GenericArguments: not { Count: > 0 } })
        {
            return ProtocolSelfTypeInfo.Instance;
        }

        // Fall back to normal type resolution
        return ResolveType(typeExpr: typeExpr);
    }

    private TypeSymbol ResolveGenericType(TypeExpression typeExpr)
    {
        TypeSymbol? genericDef = _registry.LookupType(name: typeExpr.Name);
        if (genericDef == null)
        {
            ReportError(
                SemanticDiagnosticCode.UnknownType,
                $"Unknown type '{typeExpr.Name}'.",
                typeExpr.Location);
            return ErrorTypeInfo.Instance;
        }

        if (!genericDef.IsGenericDefinition)
        {
            ReportError(
                SemanticDiagnosticCode.TypeNotGeneric,
                $"Type '{typeExpr.Name}' is not a generic type.",
                typeExpr.Location);
            return ErrorTypeInfo.Instance;
        }

        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression argExpr in typeExpr.GenericArguments!)
        {
            TypeSymbol argType = ResolveType(typeExpr: argExpr);
            typeArgs.Add(item: argType);
        }

        if (genericDef.GenericParameters!.Count != typeArgs.Count)
        {
            ReportError(
                SemanticDiagnosticCode.WrongTypeArgumentCount,
                $"Type '{typeExpr.Name}' expects {genericDef.GenericParameters.Count} type arguments, got {typeArgs.Count}.",
                typeExpr.Location);
            return ErrorTypeInfo.Instance;
        }

        // Validate generic constraints
        ValidateGenericConstraints(
            genericDef: genericDef,
            typeArgs: typeArgs,
            location: typeExpr.Location);

        return _registry.GetOrCreateInstantiation(
            genericDef: genericDef,
            typeArguments: typeArgs);
    }

    /// <summary>
    /// Validates that type arguments satisfy generic constraints.
    /// </summary>
    private void ValidateGenericConstraints(
        TypeSymbol genericDef,
        List<TypeSymbol> typeArgs,
        SourceLocation location)
    {
        if (genericDef.GenericConstraints == null || genericDef.GenericConstraints.Count == 0)
        {
            return; // No constraints to validate
        }

        // Build parameter name to type argument mapping
        var paramToArg = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < genericDef.GenericParameters!.Count; i++)
        {
            paramToArg[key: genericDef.GenericParameters[i]] = typeArgs[i];
        }

        foreach (GenericConstraintDeclaration constraint in genericDef.GenericConstraints)
        {
            if (!paramToArg.TryGetValue(key: constraint.ParameterName, value: out TypeSymbol? typeArg))
            {
                continue; // Constraint for unknown parameter
            }

            // Skip validation if type arg is a generic parameter itself
            if (typeArg is GenericParameterTypeInfo)
            {
                continue;
            }

            // Skip error types to avoid cascading errors
            if (typeArg.Category == TypeCategory.Error)
            {
                continue;
            }

            switch (constraint.ConstraintType)
            {
                case ConstraintKind.Follows:
                    ValidateFollowsConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.ValueType:
                    ValidateValueTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.ReferenceType:
                    ValidateReferenceTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.ResidentType:
                    ValidateResidentTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.RoutineType:
                    ValidateRoutineTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.ChoiceType:
                    ValidateChoiceTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.VariantType:
                    ValidateVariantTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.MutantType:
                    ValidateMutantTypeConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.ConstGeneric:
                    ValidateConstGenericConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;

                case ConstraintKind.TypeEquality:
                    ValidateTypeEqualityConstraint(typeArg: typeArg, constraint: constraint, location: location);
                    break;
            }
        }
    }

    private void ValidateFollowsConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (constraint.ConstraintTypes == null)
        {
            return;
        }

        foreach (TypeExpression protoExpr in constraint.ConstraintTypes)
        {
            if (!ImplementsProtocol(type: typeArg, protocolName: protoExpr.Name))
            {
                ReportError(
                    SemanticDiagnosticCode.ProtocolConstraintViolation,
                    $"Type '{typeArg.Name}' does not implement protocol '{protoExpr.Name}' required by constraint on '{constraint.ParameterName}'.",
                    location);
            }
        }
    }

    private void ValidateFromConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        TypeExpression baseTypeExpr = constraint.ConstraintTypes[0];
        TypeSymbol? baseType = _registry.LookupType(name: baseTypeExpr.Name);
        if (baseType == null)
        {
            return; // Base type not found, error already reported
        }

        // Check if typeArg is or inherits from baseType
        // For exact match
        if (typeArg.Name == baseType.Name || GetBaseTypeName(typeName: typeArg.Name) == baseType.Name)
        {
            return;
        }

        // TODO: Check inheritance chain when entity inheritance is fully implemented
        // For now, just check exact type match
        ReportError(
            SemanticDiagnosticCode.FromConstraintViolation,
            $"Type '{typeArg.Name}' is not '{baseType.Name}' required by constraint on '{constraint.ParameterName}'.",
            location);
    }

    private void ValidateValueTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Record)
        {
            ReportError(
                SemanticDiagnosticCode.ValueTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a value type (record) required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    private void ValidateReferenceTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Entity)
        {
            ReportError(
                SemanticDiagnosticCode.ReferenceTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a reference type (entity) required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    private void ValidateResidentTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Resident)
        {
            ReportError(
                SemanticDiagnosticCode.ResidentTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a resident type required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    private bool IsGenericParameter(string name)
    {
        if (_currentRoutine?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        if (_currentType?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        return false;
    }

    private void ValidateRoutineTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Routine)
        {
            ReportError(
                SemanticDiagnosticCode.RoutineTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a routine type required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    private void ValidateChoiceTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Choice)
        {
            ReportError(
                SemanticDiagnosticCode.ChoiceTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a choice type required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    private void ValidateVariantTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Variant)
        {
            ReportError(
                SemanticDiagnosticCode.VariantTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a variant type required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    private void ValidateMutantTypeConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Mutant)
        {
            ReportError(
                SemanticDiagnosticCode.MutantTypeConstraintViolation,
                $"Type '{typeArg.Name}' is not a mutant type required by constraint on '{constraint.ParameterName}'.",
                location);
        }
    }

    /// <summary>
    /// Validates a const generic constraint (e.g., requires N is uaddr).
    /// Const generics are compile-time constant values, not types.
    /// </summary>
    private void ValidateConstGenericConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        // The constraint specifies the required const value type (e.g., uaddr)
        TypeExpression requiredTypeExpr = constraint.ConstraintTypes[0];
        string requiredTypeName = requiredTypeExpr.Name;

        // Valid const generic types are fixed-size integers
        var validConstTypes = new HashSet<string>
        {
            "u8", "u16", "u32", "u64", "u128", "uaddr",
            "s8", "s16", "s32", "s64", "s128", "saddr",
            "letter", "byte", "bool"
        }; // TODO: instead of hardcoding, I will add some protocol?

        if (!validConstTypes.Contains(value: requiredTypeName))
        {
            ReportError(
                SemanticDiagnosticCode.InvalidConstGenericType,
                $"Invalid const generic type '{requiredTypeName}' for '{constraint.ParameterName}'. " +
                         "Const generics must be integer types.",
                location);
            return;
        }

        // Verify the type argument matches the expected const type
        if (typeArg.Name != requiredTypeName)
        {
            ReportError(
                SemanticDiagnosticCode.ConstGenericTypeMismatch,
                $"Const generic '{constraint.ParameterName}' requires type '{requiredTypeName}', got '{typeArg.Name}'.",
                location);
        }
    }

    /// <summary>
    /// Validates a type equality constraint (e.g., requires T in [s32, u8]).
    /// </summary>
    private void ValidateTypeEqualityConstraint(
        TypeSymbol typeArg,
        GenericConstraintDeclaration constraint,
        SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        // Check if typeArg matches any of the allowed types
        foreach (TypeExpression allowedExpr in constraint.ConstraintTypes)
        {
            if (typeArg.Name == allowedExpr.Name || GetBaseTypeName(typeName: typeArg.Name) == allowedExpr.Name)
            {
                return; // Found a match
            }
        }

        // No match found
        string allowedTypesList = string.Join(separator: ", ", values: constraint.ConstraintTypes.Select(selector: t => t.Name));
        ReportError(
            SemanticDiagnosticCode.TypeEqualityConstraintViolation,
            $"Type '{typeArg.Name}' is not in [{allowedTypesList}] for constraint on '{constraint.ParameterName}'.",
            location);
    }

    #endregion
}
