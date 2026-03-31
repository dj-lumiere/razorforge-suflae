namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase 1 &amp; 2: Declaration collection and type body resolution.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Phase 1: Declaration Collection

    /// <summary>
    /// Collects all type and routine declarations without resolving bodies.
    /// Creates placeholder entries in the type registry for forward references.
    /// </summary>
    /// <param name="program">The program to collect declarations from.</param>
    private void CollectDeclarations(Program program)
    {
        // #106: Validate that imports appear before other declarations
        bool seenNonImport = false;
        foreach (IAstNode declaration in program.Declarations)
        {
            if (declaration is ImportDeclaration import)
            {
                if (seenNonImport)
                {
                    ReportError(code: SemanticDiagnosticCode.ImportPositionViolation,
                        message:
                        $"Import '{import.ModulePath}' must appear before other declarations.",
                        location: import.Location);
                }
            }
            else if (declaration is not ModuleDeclaration)
            {
                seenNonImport = true;
            }
        }

        foreach (IAstNode declaration in program.Declarations)
        {
            CollectDeclaration(node: declaration);
        }
    }

    /// <summary>
    /// Collects a single declaration.
    /// </summary>
    /// <param name="node">The declaration node to collect.</param>
    private void CollectDeclaration(IAstNode node)
    {
        switch (node)
        {
            case RecordDeclaration record:
                CollectRecordDeclaration(record: record);
                break;

            case EntityDeclaration entity:
                CollectEntityDeclaration(entity: entity);
                break;

            case ChoiceDeclaration choice:
                CollectChoiceDeclaration(choice: choice);
                break;

            case FlagsDeclaration flags:
                CollectFlagsDeclaration(flags: flags);
                break;

            case VariantDeclaration variant:
                CollectVariantDeclaration(variant: variant);
                break;

            case ProtocolDeclaration protocol:
                CollectProtocolDeclaration(protocol: protocol);
                break;

            case RoutineDeclaration func:
                CollectRoutineDeclaration(routine: func);
                break;

            case ExternalDeclaration externalDecl:
                CollectExternalDeclaration(external: externalDecl);
                break;

            case ExternalBlockDeclaration block:
                foreach (Declaration decl in block.Declarations)
                {
                    CollectDeclaration(node: decl);
                }

                break;

            case VariableDeclaration variable:
                CollectMemberVariableDeclaration(memberVariable: variable);
                break;

            case ModuleDeclaration ns:
                _currentModuleName = ns.Path;
                ValidateModuleDeclaration(ns: ns);
                break;

            case ImportDeclaration import:
                ProcessImportDeclaration(import: import);
                break;

            case PresetDeclaration preset:
                CollectPresetDeclaration(preset: preset);
                break;
        }
    }

    /// <summary>
    /// Validates a module declaration.
    /// Rejects "module Core" as it's reserved for stdlib (user code cannot declare it).
    /// </summary>
    private void ValidateModuleDeclaration(ModuleDeclaration ns)
    {
        // Module "Core" is reserved for stdlib only
        if (ns.Path.Equals(value: "Core", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            !IsStdlibFile(filePath: _currentFilePath))
        {
            ReportError(code: SemanticDiagnosticCode.ReservedModuleCore,
                message:
                "Module 'Core' is reserved for the standard library and cannot be used in user code.",
                location: ns.Location);
        }
    }

    /// <summary>
    /// Processes an import declaration.
    /// Triggers on-demand module loading for the imported module.
    /// </summary>
    private void ProcessImportDeclaration(ImportDeclaration import)
    {
        // Load the module on-demand
        // This handles both Core modules and non-Core modules (Collections, ErrorHandling, etc.)
        bool success = _registry.LoadModule(importPath: import.ModulePath,
            currentFile: _currentFilePath,
            location: import.Location,
            effectiveModule: out string? effectiveModule);

        if (!success)
        {
            ReportError(code: SemanticDiagnosticCode.ModuleNotFound,
                message: $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                location: import.Location);
            return;
        }

        // #105: Check for import name collisions with specific imports
        if (import.SpecificImports != null)
        {
            foreach (string symbolName in import.SpecificImports)
            {
                if (!_importedSymbolNames.Add(item: symbolName))
                {
                    ReportError(code: SemanticDiagnosticCode.ImportNameCollision,
                        message: $"Symbol '{symbolName}' is already imported from another module.",
                        location: import.Location);
                }
            }
        }

        // Track the imported module for per-file type resolution
        if (effectiveModule != null)
        {
            _importedModules.Add(item: effectiveModule);
        }
    }

    private void CollectMemberVariableDeclaration(VariableDeclaration memberVariable)
    {
        // MemberVariables are VariableDeclarations within type members
        // Visibility is validated using the simplified four-level system:
        // - public: read/write from anywhere
        // - published: public read, private write
        // - internal: read/write within module
        // - private: read/write within file

        // Check for duplicate member variable names within the same type
        if (_currentTypeMemberVariableNames != null)
        {
            if (!_currentTypeMemberVariableNames.Add(item: memberVariable.Name))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicateMemberVariableDefinition,
                    message:
                    $"Member variable '{memberVariable.Name}' is already defined in this type.",
                    location: memberVariable.Location);
            }
        }

        if (memberVariable.Type == null)
        {
            return; // Type inference will be handled later
        }

        TypeSymbol memberVariableType = ResolveType(typeExpr: memberVariable.Type);

        // Validate that tokens cannot be stored in member variables
        ValidateNotTokenMemberVariableType(type: memberVariableType,
            memberVariableName: memberVariable.Name,
            location: memberVariable.Location);

        // Validate that variant types cannot be stored in member variables
        if (memberVariableType is VariantTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.VariantMemberVariableNotAllowed,
                message:
                $"Variant type '{memberVariableType.Name}' cannot be stored in member variable '{memberVariable.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                location: memberVariable.Location);
        }

        // Validate that Result<T> and Lookup<T> are not used as member variable types
        if (memberVariableType is ErrorHandlingTypeInfo errorHandlingType &&
            errorHandlingType.Kind is ErrorHandlingKind.Result or ErrorHandlingKind.Lookup)
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsMemberVariable,
                message:
                $"'{errorHandlingType.Kind}[T]' cannot be used as a member variable type. " +
                "Error handling types are internal for error propagation and should not be stored.",
                location: memberVariable.Location);
        }

        // TODO: Register member variable in the current type's member variable list when type body resolution is implemented
    }

    private void CollectPresetDeclaration(PresetDeclaration preset)
    {
        TypeSymbol presetType = ResolveType(typeExpr: preset.Type);
        _registry.DeclareVariable(name: preset.Name, type: presetType, isPreset: true);

        // Also register as a module-level preset for cross-file access
        string? module = GetCurrentModuleName();
        if (module != null)
        {
            _registry.RegisterPreset(name: preset.Name, type: presetType, module: module);
        }
    }

    private void CollectRecordDeclaration(RecordDeclaration record)
    {
        var typeInfo = new RecordTypeInfo(name: record.Name)
        {
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            Visibility = record.Visibility,
            Location = record.Location,
            Module = GetCurrentModuleName(),
            BackendType = ExtractLlvmAnnotation(annotations: record.Annotations)
        };

        TryRegisterType(type: typeInfo, location: record.Location);
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null)
        {
            return null;
        }

        foreach (string ann in annotations)
        {
            if (ann.StartsWith(value: "llvm(") && ann.EndsWith(value: ")"))
            {
                return ann[5..^1]
                   .Trim(trimChar: '"');
            }
        }

        return null;
    }

    private void CollectEntityDeclaration(EntityDeclaration entity)
    {
        var typeInfo = new EntityTypeInfo(name: entity.Name)
        {
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints,
            Visibility = entity.Visibility,
            Location = entity.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: entity.Location);
    }

    private void CollectChoiceDeclaration(ChoiceDeclaration choice)
    {
        var typeInfo = new ChoiceTypeInfo(name: choice.Name)
        {
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: choice.Location);
    }

    private void CollectFlagsDeclaration(FlagsDeclaration flags)
    {
        var typeInfo = new FlagsTypeInfo(name: flags.Name)
        {
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: flags.Location);
    }

    private void CollectVariantDeclaration(VariantDeclaration variant)
    {
        var typeInfo = new VariantTypeInfo(name: variant.Name)
        {
            GenericParameters = variant.GenericParameters,
            GenericConstraints = variant.GenericConstraints,
            Location = variant.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: variant.Location);
    }

    private void CollectProtocolDeclaration(ProtocolDeclaration protocol)
    {
        var typeInfo = new ProtocolTypeInfo(name: protocol.Name)
        {
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints,
            Visibility = protocol.Visibility,
            Location = protocol.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: protocol.Location);
    }

    private void CollectRoutineDeclaration(RoutineDeclaration routine)
    {
        // Determine the kind of routine
        RoutineKind kind;
        TypeSymbol? ownerType = _currentType;
        string routineName = routine.Name;

        if (_currentType != null)
        {
            // Inside a type body
            kind = routine.Name == "$create"
                ? RoutineKind.Creator
                : RoutineKind.MemberRoutine;
        }
        else if (routine.Name.Contains(value: '.'))
        {
            // Member routine syntax: "Type.routine" or "Type[T].routine"
            // Extract type name and routine name separately
            int dotIndex = routine.Name.IndexOf(value: '.');
            string typeName = routine.Name[..dotIndex];
            routineName = routine.Name[(dotIndex + 1)..]; // Just the routine name

            kind = RoutineKind.MemberRoutine;

            // Always strip generic params first (e.g., "Stack[T]" → "Stack") to look up
            // the generic definition, not a resolution cache entry.
            string lookupName = typeName.Contains(value: '[')
                ? typeName[..typeName.IndexOf(value: '[')]
                : typeName;
            ownerType = LookupTypeWithImports(name: lookupName);
        }
        else
        {
            // Top-level function
            kind = RoutineKind.Function;
        }

        // Validate that variants cannot have member routines
        if (ownerType is VariantTypeInfo && kind == RoutineKind.MemberRoutine)
        {
            ReportError(code: SemanticDiagnosticCode.VariantMethodNotAllowed,
                message: $"Variant type '{ownerType.Name}' cannot have member routines. " +
                         "Variants only support 'is', 'isnot', and pattern matching with 'when'.",
                location: routine.Location);
        }

        // Validate that choice types cannot define any operator wired methods
        if (ownerType is ChoiceTypeInfo && kind == RoutineKind.MemberRoutine &&
            IsOperatorWired(name: routineName))
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                message:
                $"Choice type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Choice types do not support operators. Use 'is' for case matching and regular routines for additional behavior.",
                location: routine.Location);
        }

        // #135: Flags types cannot define any operator wired methods
        if (ownerType is FlagsTypeInfo && kind == RoutineKind.MemberRoutine &&
            IsOperatorWired(name: routineName))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed,
                message:
                $"Flags type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Flags only support built-in operators: 'is', 'isnot', 'isonly', and 'but'.",
                location: routine.Location);
        }

        // Validate reserved prefixes (try_, check_, lookup_) for user functions
        string baseName = routineName.Contains(value: '.')
            ? routineName[(routineName.IndexOf(value: '.') + 1)..]
            : routineName;

        if (IsReservedRoutinePrefix(name: baseName))
        {
            ReportError(code: SemanticDiagnosticCode.ReservedRoutinePrefix,
                message: $"Routine name '{baseName}' uses a reserved prefix. " +
                         "Prefixes 'try_', 'check_', and 'lookup_' are reserved for auto-generated error handling variants.",
                location: routine.Location);
        }

        // Validate $ prefixed names are known built-in methods
        if (IsUnknownWiredMethod(name: baseName))
        {
            ReportError(code: SemanticDiagnosticCode.UnknownWiredRoutine,
                message: $"Routine name '{baseName}' uses reserved '$' prefix. " +
                         "Names starting with '$' are reserved for built-in methods.",
                location: routine.Location);
        }

        // @generated and @innate are only valid on protocol routine declarations
        if (routine.Annotations.Contains(item: "generated") ||
            routine.Annotations.Contains(item: "innate"))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidGeneratedInnatePlacement,
                message:
                "'@generated' and '@innate' annotations are only valid on protocol routine declarations.",
                location: routine.Location);
        }

        // @crash_only is only valid on failable (!) routines (#76)
        if (routine.Annotations.Contains(item: "crash_only") && !routine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.CrashOnlyOnNonFailable,
                message: "'@crash_only' is only valid on failable (!) routines.",
                location: routine.Location);
        }

        // #66: Index operators ($getitem/$setitem) are only valid on entities
        if (baseName is "$getitem" or "$setitem" && ownerType is not null &&
            ownerType is not EntityTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.IndexOperatorTypeKindRestriction,
                message: $"Index operators are only valid on entities, not on '{ownerType.Name}'.",
                location: routine.Location);
        }

        // #157: Conflicting mutation category annotations
        {
            int mutationCount = 0;
            if (routine.Annotations.Contains(item: "readonly"))
            {
                mutationCount++;
            }

            if (routine.Annotations.Contains(item: "writable"))
            {
                mutationCount++;
            }

            if (routine.Annotations.Contains(item: "migratable"))
            {
                mutationCount++;
            }

            if (mutationCount > 1)
            {
                ReportError(code: SemanticDiagnosticCode.MutationCategoryConflict,
                    message: "Routine has conflicting mutation annotations. " +
                             "Only one of @readonly, @writable, or @migratable can be specified.",
                    location: routine.Location);
            }
        }

        // #149: Invalid visibility combination (e.g., contradictory modifiers)
        // Visibility is a single enum, but check for annotations that conflict with visibility
        if (routine.Visibility == VisibilityModifier.Secret && ownerType == null)
        {
            ReportError(code: SemanticDiagnosticCode.InvalidVisibilityCombination,
                message:
                "Top-level routines cannot be 'secret'. Use 'core' for module-internal visibility.",
                location: routine.Location);
        }

        // #74: Validate varargs placement
        var varargParams = routine.Parameters
                                  .Where(predicate: p => p.IsVariadic)
                                  .ToList();
        if (varargParams.Count > 1)
        {
            ReportError(code: SemanticDiagnosticCode.VarargsMultiple,
                message: "Only one varargs parameter is allowed per routine.",
                location: varargParams[index: 1].Location);
        }

        if (varargParams.Count >= 1)
        {
            int varargIndex = routine.Parameters.IndexOf(item: varargParams[index: 0]);
            bool isFirstNonMe = varargIndex == 0 ||
                                varargIndex == 1 && routine.Parameters[index: 0].Name == "me";
            if (!isFirstNonMe)
            {
                ReportError(code: SemanticDiagnosticCode.VarargsNotFirst,
                    message:
                    "Varargs parameter must be the first parameter (or second after 'me').",
                    location: varargParams[index: 0].Location);
            }
        }

        // Store for deferred resolution and registration in Phase 2.5
        _pendingRoutines.Add(item: new PendingRoutine(
            Declaration: routine,
            OwnerType: ownerType,
            Kind: kind,
            RoutineName: routineName,
            Module: GetCurrentModuleName(),
            FilePath: _currentFilePath));
    }

    /// <summary>
    /// Checks if a routine name uses a reserved prefix.
    /// </summary>
    private static bool IsReservedRoutinePrefix(string name)
    {
        return name.StartsWith(value: "try_", comparisonType: StringComparison.Ordinal) ||
               name.StartsWith(value: "check_", comparisonType: StringComparison.Ordinal) ||
               name.StartsWith(value: "lookup_", comparisonType: StringComparison.Ordinal);
    }

    #endregion

    #region Phase 2.7: Protocol Implementation Validation

    /// <summary>
    /// Validates that all types declaring "obeys Protocol" implement all required protocol methods.
    /// This is called after all routines are registered (Phase 2.5) and derived operators are generated (Phase 2.6).
    /// </summary>
    private void ValidateProtocolImplementations()
    {
        foreach (TypeSymbol type in _registry.GetAllTypes())
        {
            ValidateTypeProtocolImplementation(type: type);
        }
    }

    /// <summary>
    /// Validates that a specific type implements all methods required by its declared protocols.
    /// </summary>
    private void ValidateTypeProtocolImplementation(TypeSymbol type)
    {
        // Skip stdlib/fallback types (types without source location or in Core module)
        // These are pre-defined types that may not have full method implementations in test environments
        if (type.Location == null || string.IsNullOrEmpty(value: type.Location.FileName))
        {
            return;
        }

        // Get the list of implemented protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            ChoiceTypeInfo choice => choice.ImplementedProtocols,
            FlagsTypeInfo flags => flags.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return;
        }

        // Check each protocol — skip protocols added by implicit marker conformance
        foreach (TypeSymbol protocol in implementedProtocols)
        {
            if (protocol is ProtocolTypeInfo protoInfo &&
                !_implicitProtocolConformances.Contains(item: (type.FullName, protoInfo.Name)))
            {
                ValidateProtocolMethods(type: type, protocol: protoInfo);
            }
        }
    }

    /// <summary>
    /// Validates that a type implements all methods required by a protocol.
    /// </summary>
    private void ValidateProtocolMethods(TypeSymbol type, ProtocolTypeInfo protocol)
    {
        foreach (ProtocolMethodInfo requiredMethod in protocol.Methods)
        {
            // Skip methods with default implementations
            if (requiredMethod.HasDefaultImplementation)
            {
                continue;
            }

            // Look for the method on the type (not on its protocols — that would find the protocol's own declaration)
            IEnumerable<RoutineInfo> ownMethods = _registry.GetMethodsForType(type: type);
            RoutineInfo? typeMethod =
                ownMethods.FirstOrDefault(predicate: m => m.Name == requiredMethod.Name);
            if (typeMethod == null && requiredMethod.IsFailable)
            {
                typeMethod =
                    ownMethods.FirstOrDefault(predicate: m => m.Name == requiredMethod.Name + "!");
            }

            if (typeMethod == null)
            {
                ReportError(code: SemanticDiagnosticCode.MissingProtocolMethod,
                    message:
                    $"Type '{type.Name}' declares 'obeys {protocol.Name}' but does not implement required method '{requiredMethod.Name}'.",
                    location: type.Location ?? new SourceLocation(FileName: "",
                        Line: 0,
                        Column: 0,
                        Position: 0));
            }
            else if (requiredMethod.GenerationKind == ProtocolRoutineKind.Innate &&
                     !typeMethod.IsSynthesized)
            {
                ReportError(code: SemanticDiagnosticCode.InnateOverrideNotAllowed,
                    message:
                    $"Cannot override innate routine '{protocol.Name}.{requiredMethod.Name}'. " +
                    "Innate routines are compiler-provided and cannot be overridden.",
                    location: typeMethod.Location);
            }
            else if (typeMethod != null)
            {
                // #61: Protocol mutation contract validation
                // Protocol @readonly -> impl must be @readonly
                // Protocol @writable -> impl must be @readonly or @writable (not @migratable)
                if (requiredMethod.Modification == ModificationCategory.Readonly &&
                    typeMethod.ModificationCategory != ModificationCategory.Readonly)
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMutationContractViolation,
                        message:
                        $"Protocol '{protocol.Name}' requires '{requiredMethod.Name}' to be @readonly, " +
                        $"but implementation on '{type.Name}' is @{typeMethod.ModificationCategory.ToString().ToLowerInvariant()}.",
                        location: typeMethod.Location);
                }
                else if (requiredMethod.Modification == ModificationCategory.Writable &&
                         typeMethod.ModificationCategory == ModificationCategory.Migratable)
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMutationContractViolation,
                        message:
                        $"Protocol '{protocol.Name}' requires '{requiredMethod.Name}' to be at most @writable, " +
                        $"but implementation on '{type.Name}' is @migratable.",
                        location: typeMethod.Location);
                }
            }
        }

        // Also check parent protocols
        foreach (ProtocolTypeInfo parentProtocol in protocol.ParentProtocols)
        {
            ValidateProtocolMethods(type: type, protocol: parentProtocol);
        }
    }

    #endregion

    #region Constraint Validation

    /// <summary>
    /// Validates that generic constraints only reference declared type parameters.
    /// </summary>
    /// <param name="constraints">The constraints to validate.</param>
    /// <param name="typeParameters">The declared type parameters.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateConstraintTypeParameters(
        IReadOnlyList<GenericConstraintDeclaration>? constraints,
        IReadOnlyList<string>? typeParameters, SourceLocation? location)
    {
        if (constraints == null || constraints.Count == 0)
        {
            return;
        }

        HashSet<string> validParams = typeParameters != null
            ? [..typeParameters]
            : [];

        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (!validParams.Contains(item: constraint.ParameterName))
            {
                ReportError(code: SemanticDiagnosticCode.UnknownTypeParameterInConstraint,
                    message:
                    $"Type parameter '{constraint.ParameterName}' in constraint is not declared. " +
                    $"Declared type parameters: {(typeParameters?.Count > 0 ? string.Join(separator: ", ", values: typeParameters) : "none")}.",
                    location: constraint.Location ?? location);
            }
        }
    }

    #endregion
}
