namespace Compiler.Resolution;

using SemanticVerification;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = TypeModel.Types.TypeInfo;

/// <summary>
/// Handles resolution of type expressions for the semantic analyzer.
/// </summary>
internal sealed class TypeResolver
{
    private readonly SemanticAnalyzer _sa;

    internal TypeResolver(SemanticAnalyzer sa)
    {
        _sa = sa;
    }

    #region Type Resolution

    /// <summary>
    /// Looks up a type by name, searching imported modules for the current file.
    /// Example: after "import Collections/List", module "Collections" is imported,
    /// so looking up "List" also tries "Collections.List" (module.type).
    /// </summary>
    internal TypeSymbol? LookupTypeWithImports(string name)
    {
        // Try the registry's built-in lookup (exact match + Core fallback)
        TypeSymbol? result = _sa._registry.LookupType(name: name);
        if (result != null)
        {
            return result;
        }

        // Try each imported module
        foreach (string ns in _sa._importedModules)
        {
            result = _sa._registry.LookupType(name: $"{ns}.{name}");
            if (result != null)
            {
                return result;
            }
        }

        // Try current module (user-defined types are registered as "Module.Name")
        if (_sa._currentModuleName != null && !name.Contains(value: '.'))
        {
            result = _sa._registry.LookupType(name: $"{_sa._currentModuleName}.{name}");
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a routine by name, searching the Core module and imported modules.
    /// Called after type creator resolution to avoid shadowing type creators
    /// with identically-named convenience functions (e.g., "routine U32(from: U8)").
    /// </summary>
    internal TypeModel.Symbols.RoutineInfo? LookupRoutineWithImports(string name)
    {
        // Try Core module prefix (Core routines are auto-imported)
        if (!name.Contains(value: '.'))
        {
            TypeModel.Symbols.RoutineInfo? result = _sa._registry.LookupRoutine(fullName: $"Core.{name}");
            if (result != null)
            {
                return result;
            }
        }

        // Try each imported module
        foreach (string ns in _sa._importedModules)
        {
            TypeModel.Symbols.RoutineInfo? result = _sa._registry.LookupRoutine(fullName: $"{ns}.{name}");
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

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

        // Handle tuple types from parser: Tuple(T, U, ...)
        if (typeExpr is { Name: "Tuple", GenericArguments.Count: > 0 })
        {
            var elementTypes = new List<TypeInfo>();
            foreach (TypeExpression argExpr in typeExpr.GenericArguments)
            {
                TypeSymbol argType = ResolveType(typeExpr: argExpr);
                elementTypes.Add(item: (TypeInfo)argType);
            }

            return _sa._registry.GetOrCreateTupleType(elementTypes: elementTypes);
        }

        // Handle Routine type: Routine[(T, T), Bool] → RoutineTypeInfo
        if (typeExpr is { Name: "Routine", GenericArguments.Count: 2 })
        {
            TypeExpression paramTupleExpr = typeExpr.GenericArguments[index: 0];
            TypeExpression returnTypeExpr = typeExpr.GenericArguments[index: 1];

            var paramTypes = new List<TypeInfo>();
            if (paramTupleExpr.Name == "Tuple" && paramTupleExpr.GenericArguments != null)
            {
                foreach (TypeExpression paramTypeExpr in paramTupleExpr.GenericArguments)
                {
                    paramTypes.Add(item: (TypeInfo)ResolveType(typeExpr: paramTypeExpr));
                }
            }
            else
            {
                paramTypes.Add(item: (TypeInfo)ResolveType(typeExpr: paramTupleExpr));
            }

            TypeInfo? returnType = ResolveType(typeExpr: returnTypeExpr) as TypeInfo;
            return _sa._registry.GetOrCreateRoutineType(parameterTypes: paramTypes,
                returnType: returnType,
                isFailable: false);
        }

        // Handle generic types (List<T>, Dict<K, V>, Maybe<T>)
        if (typeExpr.GenericArguments is { Count: > 0 })
        {
            return ResolveGenericType(typeExpr: typeExpr);
        }

        // Try to look up the type by name (including imported modules)
        TypeSymbol? resolved = LookupTypeWithImports(name: typeExpr.Name);
        if (resolved != null)
        {
            return resolved;
        }

        // Check for generic type parameters in current context
        if (IsGenericParameter(name: typeExpr.Name))
        {
            return new GenericParameterTypeInfo(name: typeExpr.Name);
        }

        // Check for const generic literal values (e.g., 4, 8u64, true/false)
        // These come from ParseTypeOrConstGeneric in the parser
        if (TryParseConstGenericLiteral(name: typeExpr.Name,
                value: out long constValue,
                explicitType: out string? explicitType))
        {
            return new ConstGenericValueTypeInfo(literalText: typeExpr.Name,
                value: constValue,
                explicitTypeName: explicitType);
        }

        // Check for preset constants used as const generic arguments
        TypeSymbol? presetConst = ResolvePresetConstGeneric(typeExpr: typeExpr);
        if (presetConst != null)
        {
            return presetConst;
        }

        // Type not found
        _sa.ReportError(code: SemanticDiagnosticCode.UnknownType,
            message: $"Unknown type '{typeExpr.Name}'.",
            location: typeExpr.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Resolves a type expression within a protocol context.
    /// Handles the special 'Me' type which represents the implementing type.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve.</param>
    /// <returns>The resolved type, or ProtocolSelfTypeInfo for 'Me'.</returns>
    internal TypeSymbol ResolveProtocolType(TypeExpression? typeExpr)
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

    /// <summary>
    /// Resolves a generic type expression (e.g., <c>List[T]</c>, <c>Maybe[s32]</c>) to a concrete
    /// generic resolution, looking up the base type, resolving each type argument, validating
    /// argument counts and generic constraints, and returning the cached resolved type.
    /// </summary>
    private TypeSymbol ResolveGenericType(TypeExpression typeExpr)
    {
        TypeSymbol? genericDef = LookupTypeWithImports(name: typeExpr.Name);
        if (genericDef == null)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.UnknownType,
                message: $"Unknown type '{typeExpr.Name}'.",
                location: typeExpr.Location);
            return ErrorTypeInfo.Instance;
        }

        if (!genericDef.IsGenericDefinition)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.TypeNotGeneric,
                message: $"Type '{typeExpr.Name}' is not a generic type.",
                location: typeExpr.Location);
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
            _sa.ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"Type '{typeExpr.Name}' expects {genericDef.GenericParameters.Count} type arguments, got {typeArgs.Count}.",
                location: typeExpr.Location);
            return ErrorTypeInfo.Instance;
        }

        // Reject Blank as a type argument except Result<Blank>.
        // Lookup<Blank> is ambiguous in the type_id carrier model because Blank is also the absent sentinel.
        string? genericDefCarrierName = GetCarrierBaseName(type: genericDef);
        foreach (TypeSymbol arg in typeArgs)
        {
            if (arg is not { Name: "Blank" } ||
                genericDefCarrierName is "Result")
            {
                continue;
            }

            _sa.ReportError(code: SemanticDiagnosticCode.BlankAsTypeArgument,
                message: "'Blank' cannot be used as a type argument. " +
                         "'Blank' is a unit type with no value.",
                location: typeExpr.Location);
            return ErrorTypeInfo.Instance;
        }

        // Reject Maybe<Data> — Data already supports None
        if (genericDefCarrierName == "Maybe" && typeArgs[index: 0] is { Name: "Data" })
        {
            _sa.ReportError(code: SemanticDiagnosticCode.NullableDataProhibited,
                message: "'Data?' is not allowed. 'Data' already supports 'None' natively.",
                location: typeExpr.Location);
        }

        // Reject nested Maybe types (#83): Maybe[Maybe[T]] / T??
        if (genericDefCarrierName == "Maybe" && IsMaybeType(type: typeArgs[index: 0]))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.NestedMaybeProhibited,
                message:
                "'Maybe[Maybe[T]]' is not allowed. A single '?' already expresses optionality.",
                location: typeExpr.Location);
        }

        // Validate generic constraints
        ValidateGenericConstraints(genericDef: genericDef,
            typeArgs: typeArgs,
            location: typeExpr.Location);

        return _sa._registry.GetOrCreateResolution(genericDef: genericDef, typeArguments: typeArgs);
    }

    /// <summary>
    /// Validates that type arguments satisfy generic constraints.
    /// </summary>
    internal void ValidateGenericConstraints(TypeSymbol genericDef, List<TypeSymbol> typeArgs,
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
            paramToArg[key: genericDef.GenericParameters[index: i]] = typeArgs[index: i];
        }

        foreach (GenericConstraintDeclaration constraint in genericDef.GenericConstraints)
        {
            if (!paramToArg.TryGetValue(key: constraint.ParameterName,
                    value: out TypeSymbol? typeArg))
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
                case ConstraintKind.Obeys:
                    ValidateFollowsConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.ValueType:
                    ValidateValueTypeConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.ReferenceType:
                    ValidateReferenceTypeConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.RoutineType:
                    ValidateRoutineTypeConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.ChoiceType:
                    ValidateChoiceTypeConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.VariantType:
                    ValidateVariantTypeConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.Crashable:
                    ValidateCrashableTypeConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.ConstGeneric:
                    ValidateConstGenericConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;

                case ConstraintKind.TypeEquality:
                    ValidateTypeEqualityConstraint(typeArg: typeArg,
                        constraint: constraint,
                        location: location);
                    break;
            }
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies an <c>obeys</c> constraint by implementing
    /// all of the required protocols listed in the constraint.
    /// </summary>
    private void ValidateFollowsConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (constraint.ConstraintTypes == null)
        {
            return;
        }

        foreach (TypeExpression protoExpr in constraint.ConstraintTypes)
        {
            if (!_sa.ImplementsProtocol(type: typeArg, protocolName: protoExpr.Name))
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                    message:
                    $"Type '{typeArg.Name}' does not implement protocol '{protoExpr.Name}' required by constraint on '{constraint.ParameterName}'.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>valuetype</c> constraint,
    /// requiring the argument to be a record (value type).
    /// </summary>
    private void ValidateValueTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Record)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ValueTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a value type (record) required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>referencetype</c> constraint,
    /// requiring the argument to be an entity (reference type).
    /// </summary>
    private void ValidateReferenceTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Entity)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ReferenceTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a reference type (entity) required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> is a generic type parameter declared on the
    /// currently-analyzed routine or type, allowing it to be used as a valid type reference.
    /// </summary>
    internal bool IsGenericParameter(string name)
    {
        if (_sa._currentRoutine?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        if (_sa._currentType?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        // Extension methods (routine Foo[T].bar()) — T is the owner type's generic param,
        // not the routine's own, so we also check the owner type's generic parameters.
        if (_sa._currentRoutine?.OwnerType?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>routinetype</c> constraint,
    /// requiring the argument to be a routine (function) type.
    /// </summary>
    private void ValidateRoutineTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Routine)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.RoutineTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a routine type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>choicetype</c> constraint,
    /// requiring the argument to be a choice (discriminated union of unit cases) type.
    /// </summary>
    private void ValidateChoiceTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Choice)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ChoiceTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a choice type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>varianttype</c> constraint,
    /// requiring the argument to be a variant (tagged union with payloads) type.
    /// </summary>
    private void ValidateVariantTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Variant)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.VariantTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a variant type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    private void ValidateCrashableTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Crashable)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.CrashableTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a crashable type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates a const generic constraint (e.g., requires N is Address).
    /// Const generics are build-time constant values, not types.
    /// </summary>
    private void ValidateConstGenericConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        TypeExpression requiredTypeExpr = constraint.ConstraintTypes[index: 0];
        string requiredTypeName = requiredTypeExpr.Name;

        // Resolve the required type and check ConstCompatible conformance
        TypeSymbol? requiredType = LookupTypeWithImports(name: requiredTypeName);
        if (requiredType == null)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.InvalidConstGenericType,
                message:
                $"Unknown const generic type '{requiredTypeName}' for '{constraint.ParameterName}'.",
                location: location);
            return;
        }

        // Check explicit protocol conformance OR choice category.
        // Uses explicit-only check (not structural conformance) because ConstCompatible
        // is a marker protocol — structural conformance would match any type.
        bool isValid =
            _sa.ExplicitlyImplementsProtocol(type: requiredType, protocolName: "ConstCompatible") ||
            requiredType.Category == TypeCategory.Choice;

        if (!isValid)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.InvalidConstGenericType,
                message:
                $"Type '{requiredTypeName}' is not valid for const generic '{constraint.ParameterName}'. " +
                "Const generic types must implement ConstCompatible or be a choice type.",
                location: location);
            return;
        }

        // Const generic literal values (e.g., 4, 8u64) — validate typed literals match
        if (typeArg is ConstGenericValueTypeInfo constVal)
        {
            if (constVal.ExplicitTypeName != null && constVal.ExplicitTypeName != requiredTypeName)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ConstGenericTypeMismatch,
                    message:
                    $"Const generic '{constraint.ParameterName}' requires type '{requiredTypeName}', got '{constVal.ExplicitTypeName}'.",
                    location: location);
            }

            return; // Accept — literal value satisfies const generic constraint
        }

        // Verify the type argument matches the expected const type
        if (typeArg.Name != requiredTypeName)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ConstGenericTypeMismatch,
                message:
                $"Const generic '{constraint.ParameterName}' requires type '{requiredTypeName}', got '{typeArg.Name}'.",
                location: location);
        }

        // #65: Choice const generic values must use fully-qualified names (e.g., Mode.DEBUG not bare DEBUG)
        if (requiredType.Category == TypeCategory.Choice && !typeArg.Name.Contains(value: '.'))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ConstGenericTypeMismatch,
                message:
                $"Choice const generic '{constraint.ParameterName}' requires fully-qualified case name " +
                $"(e.g., '{requiredTypeName}.{typeArg.Name}'), not bare '{typeArg.Name}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates a type equality constraint (e.g., requires T in [s32, u8]).
    /// </summary>
    private void ValidateTypeEqualityConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        // Check if typeArg matches any of the allowed types
        foreach (TypeExpression allowedExpr in constraint.ConstraintTypes)
        {
            if (typeArg.Name == allowedExpr.Name ||
                GetBaseTypeName(typeName: typeArg.Name) == allowedExpr.Name)
            {
                return; // Found a match
            }
        }

        // No match found
        string allowedTypesList = string.Join(separator: ", ",
            values: constraint.ConstraintTypes.Select(selector: t => t.Name));
        _sa.ReportError(code: SemanticDiagnosticCode.TypeEqualityConstraintViolation,
            message:
            $"Type '{typeArg.Name}' is not in [{allowedTypesList}] for constraint on '{constraint.ParameterName}'.",
            location: location);
    }

    /// <summary>
    /// Tries to parse a type expression name as a const generic literal value.
    /// Handles untyped integers (4), typed integers (4u64, 8s32), and booleans (true, false).
    /// </summary>
    private static bool TryParseConstGenericLiteral(string name, out long value,
        out string? explicitType)
    {
        value = 0;
        explicitType = null;

        // Boolean literals
        if (name == "true")
        {
            value = 1;
            explicitType = "Bool";
            return true;
        }

        if (name == "false")
        {
            value = 0;
            explicitType = "Bool";
            return true;
        }

        // Untyped integer literal (e.g., "4", "128")
        if (long.TryParse(s: name, result: out value))
        {
            return true; // No explicit type — inferred from constraint
        }

        // Typed integer literals (e.g., "4u64", "8s32")
        // Try common suffixes
        (string suffix, string typeName)[] suffixes =
        [
            ("u64", "U64"), ("s64", "S64"),
            ("u32", "U32"), ("s32", "S32"),
            ("u16", "U16"), ("s16", "S16"),
            ("u8", "U8"), ("s8", "S8"),
            ("u128", "U128"), ("s128", "S128"),
            ("addr", "Address")
        ];

        foreach ((string suffix, string typeName) in suffixes)
        {
            if (name.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s: name[..^suffix.Length], result: out value))
            {
                explicitType = typeName;
                return true;
            }
        }

        return false;
    }

    private TypeSymbol? ResolvePresetConstGeneric(TypeExpression typeExpr)
    {
        VariableInfo? preset = LookupPresetWithImports(name: typeExpr.Name);
        if (preset is not { IsPreset: true, PresetValue: not null })
        {
            return null;
        }

        return ResolvePresetConstGenericValue(preset: preset,
            useLocation: typeExpr.Location,
            visited: new HashSet<string>(StringComparer.Ordinal));
    }

    private VariableInfo? LookupPresetWithImports(string name)
    {
        VariableInfo? preset = _sa._registry.LookupVariable(name: name);
        if (preset is { IsPreset: true })
        {
            return preset;
        }

        if (_sa._currentModuleName != null && !name.Contains(value: '.'))
        {
            preset = _sa._registry.LookupVariable(name: $"{_sa._currentModuleName}.{name}");
            if (preset is { IsPreset: true })
            {
                return preset;
            }
        }

        foreach (string ns in _sa._importedModules)
        {
            preset = _sa._registry.LookupVariable(name: $"{ns}.{name}");
            if (preset is { IsPreset: true })
            {
                return preset;
            }
        }

        return null;
    }

    private TypeSymbol ResolvePresetConstGenericValue(VariableInfo preset,
        SourceLocation useLocation, HashSet<string> visited)
    {
        if (!visited.Add(item: preset.QualifiedName))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.PresetNotConstant,
                message:
                $"Preset '{preset.QualifiedName}' cannot be used as a const generic because it forms a cycle.",
                location: useLocation);
            return ErrorTypeInfo.Instance;
        }

        switch (preset.PresetValue)
        {
            case LiteralExpression literal when
                TryBuildConstGenericFromLiteral(literal: literal,
                    declaredType: preset.Type,
                    value: out ConstGenericValueTypeInfo? constValue):
                return constValue;

            case IdentifierExpression id:
            {
                VariableInfo? nestedPreset = LookupPresetWithImports(name: id.Name);
                if (nestedPreset is { IsPreset: true, PresetValue: not null })
                {
                    return ResolvePresetConstGenericValue(preset: nestedPreset,
                        useLocation: useLocation,
                        visited: visited);
                }

                break;
            }
        }

        _sa.ReportError(code: SemanticDiagnosticCode.PresetNotConstant,
            message:
            $"Preset '{preset.QualifiedName}' cannot be used as a const generic because its initializer is not a supported build-time literal.",
            location: useLocation);
        return ErrorTypeInfo.Instance;
    }

    private static bool TryBuildConstGenericFromLiteral(LiteralExpression literal,
        TypeInfo declaredType, out ConstGenericValueTypeInfo? value)
    {
        value = null;

        switch (literal.LiteralType)
        {
            case Compiler.Lexer.TokenType.True:
                value = new ConstGenericValueTypeInfo(literalText: "true",
                    value: 1,
                    explicitTypeName: "Bool");
                return true;

            case Compiler.Lexer.TokenType.False:
                value = new ConstGenericValueTypeInfo(literalText: "false",
                    value: 0,
                    explicitTypeName: "Bool");
                return true;

            case Compiler.Lexer.TokenType.Integer:
            case Compiler.Lexer.TokenType.S8Literal:
            case Compiler.Lexer.TokenType.S16Literal:
            case Compiler.Lexer.TokenType.S32Literal:
            case Compiler.Lexer.TokenType.S64Literal:
            case Compiler.Lexer.TokenType.S128Literal:
            case Compiler.Lexer.TokenType.U8Literal:
            case Compiler.Lexer.TokenType.U16Literal:
            case Compiler.Lexer.TokenType.U32Literal:
            case Compiler.Lexer.TokenType.U64Literal:
            case Compiler.Lexer.TokenType.U128Literal:
            case Compiler.Lexer.TokenType.AddressLiteral:
                if (literal.Value is string rawNumeric)
                {
                    if (TryParseConstGenericLiteral(name: rawNumeric,
                            value: out long parsed,
                            explicitType: out string? explicitType))
                    {
                        value = new ConstGenericValueTypeInfo(literalText: rawNumeric,
                            value: parsed,
                            explicitTypeName: explicitType ?? GetConstGenericExplicitTypeName(
                                declaredType: declaredType));
                        return true;
                    }
                }

                return false;
        }

        return false;
    }

    private static string? GetConstGenericExplicitTypeName(TypeInfo declaredType)
    {
        return declaredType.Name switch
        {
            "Bool" or "Address" or "U8" or "U16" or "U32" or "U64" or "U128" or
                "S8" or "S16" or "S32" or "S64" or "S128" => declaredType.Name,
            _ => null
        };
    }

    // Static helpers duplicated from SA (originally private static)

    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeInfo r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is "Maybe" or "Result" or "Lookup" ? baseName : null;
    }

    private static bool IsMaybeType(TypeSymbol type) => GetCarrierBaseName(type: type) == "Maybe";

    private static string GetBaseTypeName(string typeName)
    {
        int genericIndex = typeName.IndexOf(value: '[');
        return genericIndex >= 0
            ? typeName[..genericIndex]
            : typeName;
    }

    #endregion
}
