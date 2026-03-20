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
                    ReportError(SemanticDiagnosticCode.ImportPositionViolation,
                        $"Import '{import.ModulePath}' must appear before other declarations.",
                        import.Location);
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
        // TODO: Are these all?
        switch (node)
        {
            case RecordDeclaration record:
                CollectRecordDeclaration(record: record);
                break;

            case EntityDeclaration entity:
                CollectEntityDeclaration(entity: entity);
                break;

            case ResidentDeclaration resident:
                CollectResidentDeclaration(resident: resident);
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
                CollectFunctionDeclaration(routine: func);
                break;

            case ExternalDeclaration externalDecl:
                CollectExternalDeclaration(external: externalDecl);
                break;

            case ExternalBlockDeclaration block:
                foreach (Declaration decl in block.Declarations)
                    CollectDeclaration(node: decl);
                break;

            case VariableDeclaration variable:
                CollectMemberVariableDeclaration(memberVariable: variable);
                break;

            case ModuleDeclaration ns:
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
        if (ns.Path.Equals("Core", StringComparison.OrdinalIgnoreCase) &&
            !IsStdlibFile(_currentFilePath))
        {
            ReportError(SemanticDiagnosticCode.ReservedModuleCore,
                "Module 'Core' is reserved for the standard library and cannot be used in user code.",
                ns.Location);
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
            out string? effectiveModule);

        if (!success)
        {
            ReportError(SemanticDiagnosticCode.ModuleNotFound,
                $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                import.Location);
            return;
        }

        // #105: Check for import name collisions with specific imports
        if (import.SpecificImports != null)
        {
            foreach (string symbolName in import.SpecificImports)
            {
                if (!_importedSymbolNames.Add(item: symbolName))
                {
                    ReportError(SemanticDiagnosticCode.ImportNameCollision,
                        $"Symbol '{symbolName}' is already imported from another module.",
                        import.Location);
                }
            }
        }

        // Track the imported module for per-file type resolution
        if (effectiveModule != null)
        {
            _importedModules.Add(effectiveModule);
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
                ReportError(SemanticDiagnosticCode.DuplicateMemberVariableDefinition,
                    $"Member variable '{memberVariable.Name}' is already defined in this type.",
                    memberVariable.Location);
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
            ReportError(SemanticDiagnosticCode.VariantMemberVariableNotAllowed,
                $"Variant type '{memberVariableType.Name}' cannot be stored in member variable '{memberVariable.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                memberVariable.Location);
        }

        // Validate that Result<T> and Lookup<T> are not used as member variable types
        if (memberVariableType is ErrorHandlingTypeInfo errorHandlingType &&
            errorHandlingType.Kind is ErrorHandlingKind.Result or ErrorHandlingKind.Lookup)
        {
            ReportError(SemanticDiagnosticCode.ErrorHandlingTypeAsMemberVariable,
                $"'{errorHandlingType.Kind}[T]' cannot be used as a member variable type. " +
                "Error handling types are internal for error propagation and should not be stored.",
                memberVariable.Location);
        }

        // Entity cannot hold resident member variables (#48)
        if (_currentType is EntityTypeInfo && memberVariableType is ResidentTypeInfo)
        {
            ReportError(SemanticDiagnosticCode.EntityContainsResidentMemberVariable,
                $"Entity member variable '{memberVariable.Name}' cannot be a resident type ('{memberVariableType.Name}'). " +
                "Residents are global singletons and cannot be embedded in other types.",
                memberVariable.Location);
        }

        // TODO: Register member variable in the current type's member variable list when type body resolution is implemented
    }

    private void CollectPresetDeclaration(PresetDeclaration preset)
    {
        TypeSymbol presetType = ResolveType(typeExpr: preset.Type);
        _registry.DeclareVariable(name: preset.Name, type: presetType, isPreset: true);
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
            BackendType = ExtractLlvmAnnotation(record.Annotations)
        };

        TryRegisterType(type: typeInfo, location: record.Location);
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null) return null;
        foreach (var ann in annotations)
        {
            if (ann.StartsWith("llvm(") && ann.EndsWith(")"))
                return ann[5..^1]
                   .Trim('"');
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

    private void CollectResidentDeclaration(ResidentDeclaration resident)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(SemanticDiagnosticCode.FeatureNotInSuflae,
                "Resident types are not available in Suflae.",
                resident.Location);
            return;
        }

        var typeInfo = new ResidentTypeInfo(name: resident.Name)
        {
            GenericParameters = resident.GenericParameters,
            GenericConstraints = resident.GenericConstraints,
            Visibility = resident.Visibility,
            Location = resident.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: resident.Location);
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

    private void CollectFunctionDeclaration(RoutineDeclaration routine)
    {
        // Determine the kind of routine
        RoutineKind kind;
        TypeSymbol? ownerType = _currentType;
        string routineName = routine.Name;

        if (_currentType != null)
        {
            // Inside a type body
            kind = routine.Name == "__create__"
                ? RoutineKind.Creator
                : RoutineKind.MemberRoutine;
        }
        else if (routine.Name.Contains(value: '.'))
        {
            // Member routine syntax: "Type.routine"
            // Extract type name and routine name separately
            int dotIndex = routine.Name.IndexOf(value: '.');
            string typeName = routine.Name[..dotIndex];
            routineName = routine.Name[(dotIndex + 1)..]; // Just the routine name

            kind = RoutineKind.MemberRoutine;
            ownerType = LookupTypeWithImports(name: typeName);
        }
        else
        {
            // Top-level function
            kind = RoutineKind.Function;
        }

        // Validate that variants cannot have member routines
        if (ownerType is VariantTypeInfo && kind == RoutineKind.MemberRoutine)
        {
            ReportError(SemanticDiagnosticCode.VariantMethodNotAllowed,
                $"Variant type '{ownerType.Name}' cannot have member routines. " +
                "Variants only support 'is', 'isnot', and pattern matching with 'when'.",
                routine.Location);
        }

        // Validate that choice types cannot define any operator dunders
        if (ownerType is ChoiceTypeInfo && kind == RoutineKind.MemberRoutine &&
            IsOperatorDunder(name: routineName))
        {
            ReportError(SemanticDiagnosticCode.ArithmeticOnChoiceType,
                $"Choice type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Choice types do not support operators. Use 'is' for case matching and regular routines for additional behavior.",
                routine.Location);
        }

        // #135: Flags types cannot define any operator dunders
        if (ownerType is FlagsTypeInfo && kind == RoutineKind.MemberRoutine &&
            IsOperatorDunder(name: routineName))
        {
            ReportError(SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed,
                $"Flags type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Flags only support built-in operators: 'is', 'isnot', 'isonly', and 'but'.",
                routine.Location);
        }

        // Validate reserved prefixes (try_, check_, lookup_) for user functions
        string baseName = routineName.Contains(value: '.')
            ? routineName[(routineName.IndexOf(value: '.') + 1)..]
            : routineName;

        if (IsReservedRoutinePrefix(name: baseName))
        {
            ReportError(SemanticDiagnosticCode.ReservedRoutinePrefix,
                $"Routine name '{baseName}' uses a reserved prefix. " +
                "Prefixes 'try_', 'check_', and 'lookup_' are reserved for auto-generated error handling variants.",
                routine.Location);
        }

        // Validate dunder patterns (__name__) are known operator methods
        if (IsUnknownDunderMethod(name: baseName))
        {
            ReportError(SemanticDiagnosticCode.UnknownDunderMethod,
                $"Routine name '{baseName}' uses reserved dunder pattern. " +
                "Names matching '__name__' are reserved for operator methods.",
                routine.Location);
        }

        // @generated and @innate are only valid on protocol routine declarations
        if (routine.Annotations.Contains(item: "generated") ||
            routine.Annotations.Contains(item: "innate"))
        {
            ReportError(SemanticDiagnosticCode.InvalidGeneratedInnatePlacement,
                "'@generated' and '@innate' annotations are only valid on protocol routine declarations.",
                routine.Location);
        }

        // @crash_only is only valid on failable (!) routines (#76)
        if (routine.Annotations.Contains(item: "crash_only") && !routine.IsFailable)
        {
            ReportError(SemanticDiagnosticCode.CrashOnlyOnNonFailable,
                "'@crash_only' is only valid on failable (!) routines.",
                routine.Location);
        }

        // #66: Index operators (__getitem__/__setitem__) are only valid on entities and residents
        if (baseName is "__getitem__" or "__setitem__" && ownerType is not null &&
            ownerType is not EntityTypeInfo && ownerType is not ResidentTypeInfo)
        {
            ReportError(SemanticDiagnosticCode.IndexOperatorTypeKindRestriction,
                $"Index operators are only valid on entities and residents, not on '{ownerType.Name}'.",
                routine.Location);
        }

        // #157: Conflicting mutation category annotations
        {
            int mutationCount = 0;
            if (routine.Annotations.Contains(item: "readonly")) mutationCount++;
            if (routine.Annotations.Contains(item: "writable")) mutationCount++;
            if (routine.Annotations.Contains(item: "migratable")) mutationCount++;
            if (mutationCount > 1)
            {
                ReportError(SemanticDiagnosticCode.MutationCategoryConflict,
                    "Routine has conflicting mutation annotations. " +
                    "Only one of @readonly, @writable, or @migratable can be specified.",
                    routine.Location);
            }
        }

        // #149: Invalid visibility combination (e.g., contradictory modifiers)
        // Visibility is a single enum, but check for annotations that conflict with visibility
        if (routine.Visibility == VisibilityModifier.Secret && ownerType == null)
        {
            ReportError(SemanticDiagnosticCode.InvalidVisibilityCombination,
                "Top-level routines cannot be 'secret'. Use 'core' for module-internal visibility.",
                routine.Location);
        }

        // The AST already stores names without the '!' suffix
        // (e.g., "get!" is parsed as Name="get", IsFailable=true)
        ModificationCategory declaredModification =
            routine.Annotations.Contains(item: "readonly") ? ModificationCategory.Readonly :
            routine.Annotations.Contains(item: "writable") ? ModificationCategory.Writable :
            ModificationCategory.Migratable;

        var routineInfo = new RoutineInfo(name: routineName)
        {
            Kind = kind,
            OwnerType = ownerType,
            IsFailable = routine.IsFailable,
            IsVariadic = routine.Parameters.Any(predicate: p => p.IsVariadic),
            GenericParameters = routine.GenericParameters,
            GenericConstraints = routine.GenericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Module = GetCurrentModuleName(),
            Annotations = routine.Annotations,
            DeclaredModification = declaredModification,
            ModificationCategory = declaredModification,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage,
            AsyncStatus = routine.Async
        };

        // #74: Validate varargs placement
        var varargParams = routine.Parameters
                                  .Where(predicate: p => p.IsVariadic)
                                  .ToList();
        if (varargParams.Count > 1)
        {
            ReportError(SemanticDiagnosticCode.VarargsMultiple,
                "Only one varargs parameter is allowed per routine.",
                varargParams[1].Location);
        }

        if (varargParams.Count >= 1)
        {
            int varargIndex = routine.Parameters.IndexOf(item: varargParams[0]);
            bool isFirstNonMe = varargIndex == 0 ||
                                (varargIndex == 1 && routine.Parameters[0].Name == "me");
            if (!isFirstNonMe)
            {
                ReportError(SemanticDiagnosticCode.VarargsNotFirst,
                    "Varargs parameter must be the first parameter (or second after 'me').",
                    varargParams[0].Location);
            }
        }

        // Check for duplicate routine definitions (#150)
        if (_registry.LookupRoutine(fullName: routineInfo.FullName) != null)
        {
            ReportError(SemanticDiagnosticCode.DuplicateRoutineDefinition,
                $"Routine '{routineInfo.Name}' is already defined.",
                routine.Location);
            return;
        }

        _registry.RegisterRoutine(routine: routineInfo);
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

    /// <summary>
    /// Known dunder methods that are valid operator/special methods.
    /// </summary>
    private static readonly HashSet<string> KnownDunderMethods =
    [
        // Creator
        "__create__",

        // Arithmetic operators
        "__add__", "__sub__", "__mul__", "__truediv__", "__floordiv__", "__mod__", "__pow__",

        // Wrapping arithmetic
        "__add_wrap__", "__sub_wrap__", "__mul_wrap__", "__pow_wrap__",

        // Clamping arithmetic
        "__add_clamp__", "__sub_clamp__", "__mul_clamp__", "__truediv_clamp__", "__pow_clamp__",

        // Comparison operators
        "__eq__", "__ne__", "__lt__", "__le__", "__gt__", "__ge__", "__cmp__",

        // Bitwise operators
        "__and__", "__or__", "__xor__",
        "__ashl__", "__ashr__", "__lshl__", "__lshr__",

        // Unary operators
        "__neg__", "__not__",

        // Membership operators
        "__contains__", "__notcontains__",

        // Iteration
        "__seq__", "__next__",

        // Indexing
        "__getitem__", "__setitem__",

        // Context management
        "__enter__", "__exit__",

        // Destructor/cleanup
        "__destroy__",

        // In-place compound assignment operators
        "__iadd__", "__isub__", "__imul__", "__itruediv__", "__ifloordiv__", "__imod__",
        "__ipow__",
        "__iand__", "__ior__", "__ixor__",
        "__iashl__", "__iashr__", "__ilshl__", "__ilshr__"
    ];

    /// <summary>
    /// Checks if a routine name uses the dunder pattern but is not a known operator method.
    /// </summary>
    private static bool IsUnknownDunderMethod(string name)
    {
        // Check if it matches dunder pattern: __name__
        if (!name.StartsWith(value: "__", comparisonType: StringComparison.Ordinal) ||
            !name.EndsWith(value: "__", comparisonType: StringComparison.Ordinal) ||
            name.Length <= 4) // Must have something between the underscores
        {
            return false;
        }

        // It's a dunder pattern - check if it's known
        return !KnownDunderMethods.Contains(value: name);
    }

    /// <summary>
    /// Maps operator dunder methods to their required protocols.
    /// Types must follow the protocol to define the operator method.
    /// </summary>
    private static readonly Dictionary<string, string> DunderToProtocol = new()
    {
        // Arithmetic operators
        ["__add__"] = "Addable",
        ["__sub__"] = "Subtractable",
        ["__mul__"] = "Multiplicable",
        ["__truediv__"] = "Divisible",
        ["__floordiv__"] = "FloorDivisible",
        ["__mod__"] = "FloorDivisible",
        ["__pow__"] = "Exponentiable",

        // Wrapping arithmetic
        ["__add_wrap__"] = "WrappingAddable",
        ["__sub_wrap__"] = "WrappingSubtractable",
        ["__mul_wrap__"] = "WrappingMultiplicable",
        ["__pow_wrap__"] = "WrappingExponentiable",

        // Clamping arithmetic
        ["__add_clamp__"] = "ClampingAddable",
        ["__sub_clamp__"] = "ClampingSubtractable",
        ["__mul_clamp__"] = "ClampingMultiplicable",
        ["__truediv_clamp__"] = "ClampingDivisible",
        ["__pow_clamp__"] = "ClampingExponentiable",

        // Comparison operators
        ["__eq__"] = "Equatable",
        ["__ne__"] = "Equatable",
        ["__cmp__"] = "Comparable",
        ["__lt__"] = "Comparable",
        ["__le__"] = "Comparable",
        ["__gt__"] = "Comparable",
        ["__ge__"] = "Comparable",

        // Bitwise operators
        ["__and__"] = "Bitwiseable",
        ["__or__"] = "Bitwiseable",
        ["__xor__"] = "Bitwiseable",

        // Shift operators
        ["__ashl__"] = "Shiftable",
        ["__ashr__"] = "Shiftable",
        ["__lshl__"] = "Shiftable",
        ["__lshr__"] = "Shiftable",
        // Unary operators
        ["__neg__"] = "Negatable",
        ["__not__"] = "Invertible",

        // Container operators
        ["__contains__"] = "Container",
        ["__notcontains__"] = "Container",
        ["__getitem__"] = "Indexable",
        ["__setitem__"] = "Indexable",

        // Sequence operators
        ["__seq__"] = "Sequenceable",
        ["__next__"] = "SequenceEmitter",

        // In-place compound assignment operators
        ["__iadd__"] = "InPlaceAddable",
        ["__isub__"] = "InPlaceSubtractable",
        ["__imul__"] = "InPlaceMultiplicable",
        ["__itruediv__"] = "InPlaceDivisible",
        ["__ifloordiv__"] = "InPlaceFloorDivisible",
        ["__imod__"] = "InPlaceFloorDivisible",
        ["__ipow__"] = "InPlaceExponentiable",
        ["__iand__"] = "InPlaceBitwiseable",
        ["__ior__"] = "InPlaceBitwiseable",
        ["__ixor__"] = "InPlaceBitwiseable",
        ["__iashl__"] = "InPlaceShiftable",
        ["__iashr__"] = "InPlaceShiftable",
        ["__ilshl__"] = "InPlaceShiftable",
        ["__ilshr__"] = "InPlaceShiftable"
    };

    /// <summary>
    /// Gets the required protocol for a dunder method, or null if no protocol is required.
    /// </summary>
    private static string? GetRequiredProtocol(string dunderName)
    {
        return DunderToProtocol.GetValueOrDefault(key: dunderName);
    }

    private void CollectExternalDeclaration(ExternalDeclaration external)
    {
        // #123: Suflae cannot use C interop directly
        if (_registry.Language == Language.Suflae)
        {
            ReportError(SemanticDiagnosticCode.SuflaeNoCInterop,
                $"Suflae does not support C interop. External declaration '{external.Name}' is not allowed. " +
                "Use RazorForge for native interop.",
                external.Location);
        }

        var routineInfo = new RoutineInfo(name: external.Name)
        {
            Kind = RoutineKind.External,
            CallingConvention = external.CallingConvention,
            IsVariadic = external.IsVariadic,
            Visibility = VisibilityModifier.Open, // External declarations are always open
            Location = external.Location,
            Module = GetCurrentModuleName(),
            Annotations = external.Annotations ?? [],
            IsDangerous = external.IsDangerous
        };

        _registry.RegisterRoutine(routine: routineInfo);
    }

    private void TryRegisterType(TypeSymbol type, SourceLocation location)
    {
        try
        {
            _registry.RegisterType(type: type);
        }
        catch (InvalidOperationException)
        {
            ReportError(SemanticDiagnosticCode.DuplicateTypeDefinition,
                $"Type '{type.Name}' is already defined.",
                location);
        }
    }

    #endregion

    #region Phase 2: Type Body Resolution

    /// <summary>
    /// Resolves type bodies including member variables and method signatures.
    /// </summary>
    /// <param name="program">The program to resolve.</param>
    private void ResolveTypeBodies(Program program)
    {
        foreach (IAstNode declaration in program.Declarations)
        {
            ResolveTypeBody(node: declaration);
        }
    }

    private void ResolveTypeBody(IAstNode node)
    {
        switch (node)
        {
            case RecordDeclaration record:
                ResolveRecordBody(record: record);
                break;

            case EntityDeclaration entity:
                ResolveEntityBody(entity: entity);
                break;

            case ResidentDeclaration resident:
                ResolveResidentBody(resident: resident);
                break;

            case ProtocolDeclaration protocol:
                ResolveProtocolBody(protocol: protocol);
                break;

            case VariantDeclaration variant:
                ResolveVariantBody(variant: variant);
                break;

            case ChoiceDeclaration choice:
                ResolveChoiceBody(choice: choice);
                break;

            case FlagsDeclaration flags:
                ResolveFlagsBody(flags: flags);
                break;
        }
    }

    private void ResolveRecordBody(RecordDeclaration record)
    {
        if (record.Members.Count == 0 && !record.HasPassBody)
        {
            ReportError(SemanticDiagnosticCode.EmptyBlockWithoutPass,
                "Empty record body requires 'pass' keyword.",
                record.Location);
        }

        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeMemberVariableNames;

        _currentType = _registry.LookupType(name: record.Name);
        _currentTypeMemberVariableNames = [];

        // Resolve implemented protocols
        if (_currentType is RecordTypeInfo && record.Protocols.Count > 0)
        {
            var resolvedProtocols = new List<TypeInfo>();
            foreach (TypeExpression protoExpr in record.Protocols)
            {
                TypeSymbol protoType = ResolveType(typeExpr: protoExpr);
                if (protoType is ProtocolTypeInfo proto)
                {
                    resolvedProtocols.Add(item: proto);
                }
                else if (protoType is not ErrorTypeInfo)
                {
                    ReportError(SemanticDiagnosticCode.NotAProtocol,
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                        protoExpr.Location);
                }
            }

            // Update the type with resolved protocols
            _registry.UpdateRecordProtocols(recordName: _currentType!.FullName,
                protocols: resolvedProtocols);
        }

        // Validate generic constraints reference declared type parameters
        ValidateConstraintTypeParameters(constraints: record.GenericConstraints,
            typeParameters: record.GenericParameters,
            location: record.Location);

        // Collect member variables and other members
        var memberVariables = new List<MemberVariableInfo>();
        int memberVariableIndex = 0;

        foreach (Declaration member in record.Members)
        {
            if (member is VariableDeclaration memberVariable)
            {
                // Resolve member variable type
                TypeSymbol memberVariableType = memberVariable.Type != null
                    ? ResolveType(typeExpr: memberVariable.Type)
                    : ErrorTypeInfo.Instance;

                // Records can only contain value types + Snatched<T>
                // Entities, wrappers (Shared, Tracked, Viewed, etc.), and reference tuples are not allowed
                if (memberVariableType is TypeInfo fieldTypeInfo &&
                    fieldTypeInfo is not ErrorTypeInfo &&
                    fieldTypeInfo is not GenericParameterTypeInfo &&
                    !TypeRegistry.IsValueType(type: fieldTypeInfo) &&
                    !(fieldTypeInfo is WrapperTypeInfo { Name: "Snatched" }))
                {
                    ReportError(SemanticDiagnosticCode.RecordContainsNonValueType,
                        $"Record member variable '{memberVariable.Name}' has type '{memberVariableType.Name}' which is not a value type. " +
                        "Records can only contain value types (records, choices, variants, value tuples) and Snatched[T].",
                        memberVariable.Location);
                }

                // Create member variable info
                var memberVariableInfo =
                    new MemberVariableInfo(name: memberVariable.Name, type: memberVariableType)
                    {
                        Visibility = memberVariable.Visibility,
                        Index = memberVariableIndex++,
                        HasDefaultValue = memberVariable.Initializer != null,
                        Location = memberVariable.Location,
                        Owner = _currentType
                    };

                memberVariables.Add(item: memberVariableInfo);
            }

            // Still call CollectDeclaration for validation and other member types
            CollectDeclaration(node: member);
        }

        // Update the record with resolved member variables
        if (memberVariables.Count > 0)
        {
            _registry.UpdateRecordMemberVariables(recordName: _currentType!.FullName,
                memberVariables: memberVariables);
        }

        _currentType = previousType;
        _currentTypeMemberVariableNames = previousFieldNames;
    }

    private void ResolveEntityBody(EntityDeclaration entity)
    {
        if (entity.Members.Count == 0 && !entity.HasPassBody)
        {
            ReportError(SemanticDiagnosticCode.EmptyBlockWithoutPass,
                "Empty entity body requires 'pass' keyword.",
                entity.Location);
        }

        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeMemberVariableNames;

        _currentType = _registry.LookupType(name: entity.Name);
        _currentTypeMemberVariableNames = [];

        // Resolve implemented protocols
        if (_currentType is EntityTypeInfo && entity.Protocols.Count > 0)
        {
            var resolvedProtocols = new List<TypeInfo>();
            foreach (TypeExpression protoExpr in entity.Protocols)
            {
                TypeSymbol protoType = ResolveType(typeExpr: protoExpr);
                if (protoType is ProtocolTypeInfo proto)
                {
                    resolvedProtocols.Add(item: proto);
                }
                else if (protoType is not ErrorTypeInfo)
                {
                    ReportError(SemanticDiagnosticCode.NotAProtocol,
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                        protoExpr.Location);
                }
            }

            _registry.UpdateEntityProtocols(entityName: _currentType!.FullName,
                protocols: resolvedProtocols);
        }

        // Collect member variables and other members
        var memberVariables = new List<MemberVariableInfo>();
        int memberVariableIndex = 0;

        foreach (Declaration member in entity.Members)
        {
            if (member is VariableDeclaration memberVariable)
            {
                TypeSymbol memberVariableType = memberVariable.Type != null
                    ? ResolveType(typeExpr: memberVariable.Type)
                    : ErrorTypeInfo.Instance;

                var memberVariableInfo =
                    new MemberVariableInfo(name: memberVariable.Name, type: memberVariableType)
                    {
                        Visibility = memberVariable.Visibility,
                        Index = memberVariableIndex++,
                        HasDefaultValue = memberVariable.Initializer != null,
                        Location = memberVariable.Location,
                        Owner = _currentType
                    };

                memberVariables.Add(item: memberVariableInfo);
            }

            CollectDeclaration(node: member);
        }

        if (memberVariables.Count > 0)
        {
            _registry.UpdateEntityMemberVariables(entityName: _currentType!.FullName,
                memberVariables: memberVariables);
        }

        _currentType = previousType;
        _currentTypeMemberVariableNames = previousFieldNames;
    }

    private void ResolveResidentBody(ResidentDeclaration resident)
    {
        if (resident.Members.Count == 0 && !resident.HasPassBody)
        {
            ReportError(SemanticDiagnosticCode.EmptyBlockWithoutPass,
                "Empty resident body requires 'pass' keyword.",
                resident.Location);
        }

        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeMemberVariableNames;

        _currentType = _registry.LookupType(name: resident.Name);
        _currentTypeMemberVariableNames = [];

        // Resolve implemented protocols
        if (_currentType is ResidentTypeInfo && resident.Protocols.Count > 0)
        {
            var resolvedProtocols = new List<TypeInfo>();
            foreach (TypeExpression protoExpr in resident.Protocols)
            {
                TypeSymbol protoType = ResolveType(typeExpr: protoExpr);
                if (protoType is ProtocolTypeInfo proto)
                {
                    resolvedProtocols.Add(item: proto);

                    // #55: Residents cannot implement Hashable
                    if (proto.Name == "Hashable")
                    {
                        ReportError(SemanticDiagnosticCode.ResidentHashableProhibited,
                            $"Resident type '{resident.Name}' cannot implement Hashable. " +
                            "Residents are identity-based, not content-based.",
                            protoExpr.Location);
                    }
                }
                else if (protoType is not ErrorTypeInfo)
                {
                    ReportError(SemanticDiagnosticCode.NotAProtocol,
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                        protoExpr.Location);
                }
            }

            _registry.UpdateResidentProtocols(residentName: _currentType!.FullName,
                protocols: resolvedProtocols);
        }

        // Collect member variables and other members
        var memberVariables = new List<MemberVariableInfo>();
        int memberVariableIndex = 0;

        foreach (Declaration member in resident.Members)
        {
            if (member is VariableDeclaration memberVariable)
            {
                TypeSymbol memberVariableType = memberVariable.Type != null
                    ? ResolveType(typeExpr: memberVariable.Type)
                    : ErrorTypeInfo.Instance;

                // #53: Resident member variables can only contain records, primitives, Snatched[T], or other residents
                if (memberVariableType is TypeInfo fieldTypeInfo &&
                    fieldTypeInfo is not ErrorTypeInfo &&
                    fieldTypeInfo is not GenericParameterTypeInfo &&
                    !IsValidResidentFieldType(type: fieldTypeInfo))
                {
                    ReportError(SemanticDiagnosticCode.ResidentContainsInvalidType,
                        $"Resident member variable '{memberVariable.Name}' has type '{memberVariableType.Name}' which is not valid. " +
                        "Resident member variables can only contain records, primitives, Snatched[T], or other residents.",
                        memberVariable.Location);
                }

                var memberVariableInfo =
                    new MemberVariableInfo(name: memberVariable.Name, type: memberVariableType)
                    {
                        Visibility = memberVariable.Visibility,
                        Index = memberVariableIndex++,
                        HasDefaultValue = memberVariable.Initializer != null,
                        Location = memberVariable.Location,
                        Owner = _currentType
                    };

                memberVariables.Add(item: memberVariableInfo);
            }

            CollectDeclaration(node: member);
        }

        if (memberVariables.Count > 0)
        {
            _registry.UpdateResidentMemberVariables(residentName: _currentType!.FullName,
                memberVariables: memberVariables);
        }

        _currentType = previousType;
        _currentTypeMemberVariableNames = previousFieldNames;
    }

    private void ResolveProtocolBody(ProtocolDeclaration protocol)
    {
        // Look up the registered protocol type
        TypeSymbol? protoType = _registry.LookupType(name: protocol.Name);
        if (protoType is not ProtocolTypeInfo protocolInfo)
        {
            return;
        }

        // Resolve parent protocols (protocol X obeys Y, Z)
        var parentProtocols = new List<ProtocolTypeInfo>();
        foreach (TypeExpression parentExpr in protocol.ParentProtocols)
        {
            TypeSymbol parentType = ResolveType(typeExpr: parentExpr);
            if (parentType is ProtocolTypeInfo parentProtocol)
            {
                parentProtocols.Add(item: parentProtocol);
            }
            else if (parentType is not ErrorTypeInfo)
            {
                ReportError(SemanticDiagnosticCode.NotAProtocol,
                    $"'{parentExpr}' is not a protocol. Only protocols can be inherited with 'obeys'.",
                    parentExpr.Location);
            }
        }

        // Convert method signatures to ProtocolMethodInfo
        var methods = new List<ProtocolMethodInfo>();
        foreach (RoutineSignature sig in protocol.Methods)
        {
            bool isFailable = sig.Name.EndsWith(value: '!');
            string fullName = isFailable
                ? sig.Name[..^1]
                : sig.Name;

            // Check if this is an instance method (has "Me." prefix)
            // Protocol methods: "Me.methodName" = instance, "methodName" = type-level
            bool isInstanceMethod = fullName.StartsWith(value: "Me.");
            string methodName = isInstanceMethod
                ? fullName[3..]
                : fullName;

            // Resolve parameter types (skip 'me' if it appears as explicit parameter)
            var paramTypes = new List<TypeSymbol>();
            var paramNames = new List<string>();
            foreach (Parameter param in sig.Parameters)
            {
                // Skip the 'me' parameter - it's implicit for instance methods
                if (param.Name == "me")
                {
                    continue;
                }

                TypeSymbol paramType = ResolveProtocolType(typeExpr: param.Type);
                paramTypes.Add(item: paramType);
                paramNames.Add(item: param.Name);
            }

            // Resolve return type
            TypeSymbol? returnType = sig.ReturnType != null
                ? ResolveProtocolType(typeExpr: sig.ReturnType)
                : null;

            // Extract modification category from attributes
            // @readonly -> Readonly, @writable -> Writable, default/no annotation -> Migratable
            ModificationCategory modification = ModificationCategory.Migratable; // Default
            if (sig.Annotations != null)
            {
                if (sig.Annotations.Contains(item: "readonly"))
                {
                    modification = ModificationCategory.Readonly;
                }
                else if (sig.Annotations.Contains(item: "writable"))
                {
                    modification = ModificationCategory.Writable;
                }
                // else: "migratable" or no annotation = Migratable (default)
            }

            // Extract generation kind from annotations
            ProtocolRoutineKind generationKind = ProtocolRoutineKind.None;
            if (sig.Annotations?.Contains(item: "innate") == true)
            {
                generationKind = ProtocolRoutineKind.Innate;
            }
            else if (sig.Annotations?.Contains(item: "generated") == true)
            {
                generationKind = ProtocolRoutineKind.Generated;
            }

            var methodInfo = new ProtocolMethodInfo(name: methodName)
            {
                IsInstanceMethod = isInstanceMethod,
                Modification = modification,
                GenerationKind = generationKind,
                ParameterTypes = paramTypes,
                ParameterNames = paramNames,
                ReturnType = returnType,
                IsFailable = isFailable,
                HasDefaultImplementation = false, // Abstract protocol methods have no default
                Location = sig.Location
            };

            methods.Add(item: methodInfo);
        }

        // Update the protocol with resolved methods and parent protocols
        var updatedProtocol = new ProtocolTypeInfo(name: protocol.Name)
        {
            Methods = methods,
            ParentProtocols = parentProtocols,
            GenericParameters = protocolInfo.GenericParameters,
            GenericConstraints = protocolInfo.GenericConstraints,
            Visibility = protocolInfo.Visibility,
            Location = protocolInfo.Location,
            Module = protocolInfo.Module
        };

        // Replace the protocol in the registry
        _registry.UpdateType(oldType: protocolInfo, newType: updatedProtocol);
    }

    private void ResolveVariantBody(VariantDeclaration variant)
    {
        if (variant.Cases.Count == 0)
        {
            ReportError(SemanticDiagnosticCode.EmptyEnumerationBody,
                $"Variant type '{variant.Name}' must have at least one case.",
                variant.Location);
            return;
        }

        // Validate each variant case's payload type
        foreach (VariantCase variantCase in variant.Cases)
        {
            if (variantCase.AssociatedTypes == null)
            {
                continue; // No payload for this case
            }

            TypeSymbol payloadType = ResolveType(typeExpr: variantCase.AssociatedTypes);

            // Validate that tokens cannot be used as variant payloads
            ValidateNotTokenVariantPayload(type: payloadType,
                caseName: variantCase.Name,
                location: variantCase.Location);

            // #59: Variant cases cannot hold nested variants, Result[T], or Lookup[T]
            if (payloadType is VariantTypeInfo)
            {
                ReportError(SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                    $"Variant case '{variantCase.Name}' cannot contain nested variant type '{payloadType.Name}'.",
                    variantCase.Location);
            }
            else if (payloadType is ErrorHandlingTypeInfo
                     {
                         Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup
                     })
            {
                ReportError(SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                    $"Variant case '{variantCase.Name}' cannot contain '{payloadType.Name}'. " +
                    "Use failable routines (!) instead of storing Result/Lookup in variants.",
                    variantCase.Location);
            }
        }
    }

    /// <summary>
    /// Resolves choice body, populating the choice cases.
    /// </summary>
    private void ResolveChoiceBody(ChoiceDeclaration choice)
    {
        if (choice.Cases.Count == 0)
        {
            ReportError(SemanticDiagnosticCode.EmptyEnumerationBody,
                $"Choice type '{choice.Name}' must have at least one case.",
                choice.Location);
            return;
        }

        TypeSymbol? choiceType = _registry.LookupType(name: choice.Name);
        if (choiceType is not ChoiceTypeInfo choiceInfo)
        {
            return;
        }

        var cases = new List<ChoiceCaseInfo>();
        long autoValue = 0;

        foreach (ChoiceCase caseDecl in choice.Cases)
        {
            long? explicitValue = null;

            // Evaluate explicit value if provided
            if (caseDecl.Value != null)
            {
                explicitValue = TryEvaluateChoiceCaseValue(expression: caseDecl.Value,
                    choice: choice,
                    caseName: caseDecl.Name,
                    location: caseDecl.Location);
                if (explicitValue.HasValue)
                {
                    autoValue = explicitValue.Value;
                    // Check auto-increment overflow
                    if (autoValue == long.MaxValue)
                    {
                        // Next auto-increment would overflow; only report if there are more cases after this
                        // The overflow will be caught when the next case tries to use autoValue + 1
                    }
                    else
                    {
                        autoValue += 1;
                    }
                }
            }

            long computedValue;
            if (explicitValue.HasValue)
            {
                computedValue = explicitValue.Value;
            }
            else
            {
                computedValue = autoValue;
                if (autoValue == long.MaxValue)
                {
                    ReportError(SemanticDiagnosticCode.ChoiceCaseValueOverflow,
                        $"Choice '{choice.Name}' case '{caseDecl.Name}': auto-assigned value would overflow S64 range.",
                        caseDecl.Location);
                }
                else
                {
                    autoValue += 1;
                }
            }

            cases.Add(new ChoiceCaseInfo(name: caseDecl.Name)
            {
                Value = explicitValue,
                ComputedValue = computedValue,
                Location = caseDecl.Location
            });
        }

        // Validate all-or-nothing explicit values
        int explicitCount = choice.Cases.Count(c => c.Value != null);
        if (explicitCount > 0 && explicitCount < choice.Cases.Count)
        {
            ReportError(SemanticDiagnosticCode.ChoiceMixedValues,
                $"Choice '{choice.Name}' mixes explicit and implicit case values. " +
                "Either all cases must have explicit values or none should.",
                choice.Location);
        }

        // Validate no duplicate computed values
        var seenValues = new Dictionary<long, string>();
        foreach (ChoiceCaseInfo caseInfo in cases)
        {
            if (seenValues.TryGetValue(key: caseInfo.ComputedValue,
                    value: out string? existingCase))
            {
                ReportError(SemanticDiagnosticCode.ChoiceDuplicateValue,
                    $"Choice '{choice.Name}' case '{caseInfo.Name}' has the same value ({caseInfo.ComputedValue}) as case '{existingCase}'.",
                    caseInfo.Location ?? choice.Location);
            }
            else
            {
                seenValues[caseInfo.ComputedValue] = caseInfo.Name;
            }
        }

        // Update the choice with resolved cases
        _registry.UpdateChoiceCases(choiceName: choiceInfo.FullName, cases: cases);
    }

    private void ResolveFlagsBody(FlagsDeclaration flags)
    {
        if (flags.Members.Count == 0)
        {
            ReportError(SemanticDiagnosticCode.EmptyEnumerationBody,
                $"Flags type '{flags.Name}' must have at least one member.",
                flags.Location);
            return;
        }

        TypeSymbol? flagsType = _registry.LookupType(name: flags.Name);
        if (flagsType is not FlagsTypeInfo flagsInfo)
        {
            return;
        }

        // #127: Max 64 members (U64 backing)
        if (flags.Members.Count > 64)
        {
            ReportError(SemanticDiagnosticCode.FlagsTooManyMembers,
                $"Flags type '{flags.Name}' has {flags.Members.Count} members, but the maximum is 64.",
                flags.Location);
        }

        var members = new List<FlagsMemberInfo>();
        var seenNames = new HashSet<string>();

        for (int i = 0; i < flags.Members.Count; i++)
        {
            string memberName = flags.Members[i];

            // Validate no duplicate member names
            if (!seenNames.Add(memberName))
            {
                ReportError(SemanticDiagnosticCode.FlagsDuplicateMember,
                    $"Flags type '{flags.Name}' has duplicate member '{memberName}'.",
                    flags.Location);
                continue;
            }

            members.Add(new FlagsMemberInfo(Name: memberName, BitPosition: i));
        }

        _registry.UpdateFlagsMembers(flagsName: flagsInfo.FullName, members: members);
    }

    /// <summary>
    /// Evaluates a choice case value expression to a long integer.
    /// Handles positive literals, negative unary expressions, and reports errors for invalid values.
    /// </summary>
    private long? TryEvaluateChoiceCaseValue(Expression expression, ChoiceDeclaration choice,
        string caseName, SourceLocation location)
    {
        // Positive integer literal
        if (expression is LiteralExpression literal)
        {
            return TryConvertLiteralToLong(value: literal.Value,
                choice: choice,
                caseName: caseName,
                location: location);
        }

        // Negative integer literal: -N
        if (expression is UnaryExpression
            {
                Operator: UnaryOperator.Minus, Operand: LiteralExpression negLiteral
            })
        {
            long? positiveValue = TryConvertLiteralToLong(value: negLiteral.Value,
                choice: choice,
                caseName: caseName,
                location: location);
            if (positiveValue.HasValue)
            {
                // Handle long.MinValue edge case: -(-9223372036854775808) can't be represented
                // But negating a positive value is fine for all values 0..long.MaxValue
                return -positiveValue.Value;
            }

            return null;
        }

        ReportError(SemanticDiagnosticCode.ChoiceCaseValueOverflow,
            $"Choice '{choice.Name}' case '{caseName}': value must be an integer literal.",
            location);
        return null;
    }

    /// <summary>
    /// Converts a literal object value to a long for choice case storage.
    /// The parser stores numeric literals as strings, so we parse them here.
    /// </summary>
    private long? TryConvertLiteralToLong(object value, ChoiceDeclaration choice, string caseName,
        SourceLocation location)
    {
        if (value is string strVal)
        {
            string cleaned = CleanNumericLiteral(strVal);
            if (TryParseSignedInteger(cleaned, out long result))
            {
                return result;
            }
        }

        return ReportChoiceValueError(choice: choice, caseName: caseName, location: location);
    }

    private long? ReportChoiceValueError(ChoiceDeclaration choice, string caseName,
        SourceLocation location)
    {
        ReportError(SemanticDiagnosticCode.ChoiceCaseValueOverflow,
            $"Choice '{choice.Name}' case '{caseName}': value must be an integer literal within S64 range.",
            location);
        return null;
    }

    #endregion

    #region Phase 2.5: Routine Signature Resolution

    /// <summary>
    /// Resolves routine signatures including parameter types.
    /// Performs protocol-as-type desugaring (routine foo(x: Displayable) → routine foo&lt;T obeys Displayable&gt;(x: T)).
    /// </summary>
    /// <param name="program">The program to resolve.</param>
    private void ResolveRoutineSignatures(Program program)
    {
        foreach (IAstNode declaration in program.Declarations)
        {
            ResolveRoutineSignature(node: declaration);
        }
    }

    private void ResolveRoutineSignature(IAstNode node)
    {
        switch (node)
        {
            case RoutineDeclaration routine:
                ResolveRoutineParameters(routine: routine);
                break;

            case RecordDeclaration record:
                foreach (Declaration member in record.Members)
                {
                    ResolveRoutineSignature(node: member);
                }

                break;

            case EntityDeclaration entity:
                foreach (Declaration member in entity.Members)
                {
                    ResolveRoutineSignature(node: member);
                }

                break;

            case ResidentDeclaration resident:
                foreach (Declaration member in resident.Members)
                {
                    ResolveRoutineSignature(node: member);
                }

                break;

            case ExternalDeclaration externalDecl:
                ResolveExternalParameters(externalDecl);
                break;

            case ExternalBlockDeclaration block:
                foreach (Declaration decl in block.Declarations)
                    ResolveRoutineSignature(node: decl);
                break;
        }
    }

    /// <summary>
    /// Resolves parameters for a routine declaration, performing protocol-as-type desugaring.
    /// </summary>
    private void ResolveRoutineParameters(RoutineDeclaration routine)
    {
        bool isFailable = routine.Name.EndsWith(value: '!');
        string routineName = isFailable
            ? routine.Name[..^1]
            : routine.Name;

        // For extension methods (Type.method), the routine was registered with just the method name
        // but the FullName includes the owner type, so we can look it up either way
        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: routineName);
        if (routineInfo == null)
        {
            return;
        }

        var parameters = new List<ParameterInfo>();
        var implicitGenerics = new List<string>();
        var implicitConstraints = new List<GenericConstraintDeclaration>();
        int implicitGenericCounter = 0;

        foreach (Parameter param in routine.Parameters)
        {
            if (param.Type == null)
            {
                // #36: Suflae untyped parameters default to Data
                if (_registry.Language == Language.Suflae)
                {
                    TypeSymbol dataType =
                        _registry.LookupType(name: "Data") ?? ErrorTypeInfo.Instance;
                    parameters.Add(item: new ParameterInfo(name: param.Name, type: dataType));
                }
                else
                {
                    // Type inference required - handle later
                    parameters.Add(item: new ParameterInfo(name: param.Name,
                        type: ErrorTypeInfo.Instance));
                }

                continue;
            }

            TypeSymbol paramType = ResolveType(typeExpr: param.Type);

            // #74: Varargs parameter gets wrapped as List[T]
            if (param.IsVariadic)
            {
                TypeSymbol? listDef = _registry.LookupType(name: "List");
                if (listDef != null)
                {
                    paramType = _registry.GetOrCreateResolution(genericDef: listDef,
                        typeArguments: [paramType]);
                }
            }

            // Validate that variant types cannot be used as parameter types
            if (paramType is VariantTypeInfo)
            {
                ReportError(SemanticDiagnosticCode.VariantParameterNotAllowed,
                    $"Variant type '{paramType.Name}' cannot be used as a parameter type. " +
                    "Return variants from routines and dismantle them with pattern matching.",
                    param.Location);
            }

            // Validate that Result<T> and Lookup<T> are not used as parameter types
            if (paramType is ErrorHandlingTypeInfo errorHandlingType &&
                errorHandlingType.Kind is ErrorHandlingKind.Result or ErrorHandlingKind.Lookup)
            {
                ReportError(SemanticDiagnosticCode.ErrorHandlingTypeAsParameter,
                    $"'{errorHandlingType.Kind}[T]' cannot be used as a parameter type. " +
                    "Error handling types are internal for error propagation and should not be passed as arguments.",
                    param.Location);
            }

            // Protocol-as-type desugaring: routine foo(x: Displayable) → routine foo[T obeys Displayable](x: T)
            if (paramType is ProtocolTypeInfo)
            {
                // Generate implicit generic parameter name
                string implicitGenericName = $"__T{implicitGenericCounter++}";
                implicitGenerics.Add(item: implicitGenericName);

                // Create "obeys" constraint for the implicit generic
                var constraint = new GenericConstraintDeclaration(
                    ParameterName: implicitGenericName,
                    ConstraintType: ConstraintKind.Obeys,
                    ConstraintTypes: [param.Type],
                    Location: param.Location);
                implicitConstraints.Add(item: constraint);

                // Use the implicit generic as the parameter type
                var genericParamType = new GenericParameterTypeInfo(name: implicitGenericName)
                {
                    Location = param.Location
                };

                parameters.Add(item: new ParameterInfo(name: param.Name, type: genericParamType)
                {
                    DefaultValue = param.DefaultValue
                });
            }
            else
            {
                parameters.Add(item: new ParameterInfo(name: param.Name, type: paramType)
                {
                    DefaultValue = param.DefaultValue
                });
            }
        }

        // Resolve return type
        TypeSymbol? returnType = routine.ReturnType != null
            ? ResolveType(typeExpr: routine.ReturnType)
            : null;

        // Validate that Maybe<T>/Result<T>/Lookup<T> are not used as return types
        // These are builder-generated wrapper types for failable routines (!)
        if (returnType is ErrorHandlingTypeInfo errorHandlingReturn &&
            errorHandlingReturn.Kind is ErrorHandlingKind.Maybe or ErrorHandlingKind.Result
                or ErrorHandlingKind.Lookup && !IsStdlibFile(_currentFilePath))
        {
            ReportError(SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType,
                $"Routine cannot return '{errorHandlingReturn.Kind}[T]'. " +
                "These types are builder-generated for failable routines. " +
                "Use a failable routine (!) with 'throw'/'absent' instead.",
                routine.ReturnType?.Location ?? routine.Location);
        }

        // Merge implicit generics with explicit generics
        List<string> allGenericParams = routineInfo.GenericParameters?.ToList() ?? [];
        allGenericParams.AddRange(collection: implicitGenerics);

        // Merge implicit constraints with explicit constraints
        List<GenericConstraintDeclaration> allConstraints =
            routineInfo.GenericConstraints?.ToList() ?? [];
        allConstraints.AddRange(collection: implicitConstraints);

        // Update the routine info with resolved parameters
        _registry.UpdateRoutine(routine: routineInfo,
            parameters: parameters,
            returnType: returnType,
            genericParameters: allGenericParams.Count > 0
                ? allGenericParams
                : null,
            genericConstraints: allConstraints.Count > 0
                ? allConstraints
                : null);

        // Re-lookup the updated routine for validation
        RoutineInfo? updatedRoutineInfo = _registry.LookupRoutine(fullName: routineInfo.FullName);
        if (updatedRoutineInfo == null)
        {
            return;
        }

        // Validate operator protocol conformance for dunder methods
        ValidateOperatorProtocolConformance(routineInfo: updatedRoutineInfo,
            location: routine.Location);

        // Validate that the method matches the protocol signature if the type declares following a protocol
        ValidateProtocolMethodSignature(routineInfo: updatedRoutineInfo,
            location: routine.Location);
    }

    /// <summary>
    /// Validates that a method's signature matches the protocol method it implements.
    /// </summary>
    private void ValidateProtocolMethodSignature(RoutineInfo routineInfo, SourceLocation? location)
    {
        // Only check methods (not functions)
        if (routineInfo.OwnerType == null)
        {
            return;
        }

        // Re-lookup the owner type to get the updated version with protocols
        TypeSymbol? currentOwnerType = _registry.LookupType(name: routineInfo.OwnerType.Name);
        if (currentOwnerType == null)
        {
            return;
        }

        // Get the list of implemented protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = currentOwnerType switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            ResidentTypeInfo resident => resident.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return;
        }

        // Check each protocol for a method with this name
        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented is not ProtocolTypeInfo protocol)
            {
                continue;
            }

            // Find the protocol method with this name
            ProtocolMethodInfo? protoMethod = protocol.Methods.FirstOrDefault(
                predicate: m => m.Name == routineInfo.Name);

            if (protoMethod == null)
            {
                continue;
            }

            // Validate the signature matches
            ValidateMethodAgainstProtocol(typeMethod: routineInfo,
                protoMethod: protoMethod,
                protocol: protocol,
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type method matches the expected protocol method signature.
    /// Reports specific errors for mismatches.
    /// </summary>
    private void ValidateMethodAgainstProtocol(RoutineInfo typeMethod,
        ProtocolMethodInfo protoMethod, ProtocolTypeInfo protocol, SourceLocation? location)
    {
        // Check failable matches
        if (typeMethod.IsFailable != protoMethod.IsFailable)
        {
            string expected = protoMethod.IsFailable
                ? "failable (!)"
                : "non-failable";
            string actual = typeMethod.IsFailable
                ? "failable (!)"
                : "non-failable";
            ReportError(SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                $"Method '{typeMethod.Name}' should be {expected} to match protocol '{protocol.Name}', but is {actual}.",
                location);
            return;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body methods have explicit 'me' as first parameter
        // Extension methods don't include 'me' in the parameter list
        int expectedParamCount = protoMethod.ParameterTypes.Count;
        bool hasMeParam = typeMethod.Parameters.Count > 0 && typeMethod.Parameters[0].Name == "me";
        int actualParamCount = typeMethod.Parameters.Count - (hasMeParam
            ? 1
            : 0);

        if (actualParamCount != expectedParamCount)
        {
            ReportError(SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                $"Method '{typeMethod.Name}' has {actualParamCount} parameter(s) but protocol '{protocol.Name}' expects {expectedParamCount}.",
                location);
            return;
        }

        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam
            ? 1
            : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMethod.ParameterTypes[i];
            TypeSymbol actualType = typeMethod.Parameters[startIndex + i].Type;

            // Handle protocol self type (Me) - should match the owner type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                if (typeMethod.OwnerType != null && actualType.Name != typeMethod.OwnerType.Name)
                {
                    ReportError(SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        $"Parameter '{protoMethod.ParameterNames[i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location);
                }
            }
            else if (actualType.Name != expectedType.Name)
            {
                ReportError(SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                    $"Parameter '{protoMethod.ParameterNames[i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{expectedType.Name}'.",
                    location);
            }
        }

        // Check return type
        if (protoMethod.ReturnType != null && typeMethod.ReturnType != null)
        {
            TypeSymbol expectedReturn = protoMethod.ReturnType;
            TypeSymbol actualReturn = typeMethod.ReturnType;

            // Handle protocol self type (Me)
            if (expectedReturn is ProtocolSelfTypeInfo)
            {
                if (typeMethod.OwnerType != null && actualReturn.Name != typeMethod.OwnerType.Name)
                {
                    ReportError(SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location);
                }
            }
            else if (actualReturn.Name != expectedReturn.Name)
            {
                ReportError(SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                    $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{expectedReturn.Name}'.",
                    location);
            }
        }
    }

    /// <summary>
    /// Validates that a type obeys the required protocol when defining operator methods.
    /// For example, defining __add__ requires the type to obey Addable.
    /// </summary>
    private void ValidateOperatorProtocolConformance(RoutineInfo routineInfo,
        SourceLocation? location)
    {
        // Only check methods (not functions)
        if (routineInfo.OwnerType == null)
        {
            return;
        }

        // Get the required protocol for this dunder method
        string? requiredProtocol = GetRequiredProtocol(dunderName: routineInfo.Name);
        if (requiredProtocol == null)
        {
            return; // Not an operator method or no protocol required
        }

        // Re-lookup the owner type to get the updated version with protocols
        // (the RoutineInfo.OwnerType may reference an older object from Phase 1)
        TypeSymbol? currentOwnerType = _registry.LookupType(name: routineInfo.OwnerType.Name);
        if (currentOwnerType == null)
        {
            return;
        }

        // Check if the owner type EXPLICITLY obeys the required protocol
        // (structural conformance doesn't count - you must declare "obeys Protocol")
        if (!ExplicitlyFollowsProtocol(type: currentOwnerType, protocolName: requiredProtocol))
        {
            ReportError(SemanticDiagnosticCode.OperatorWithoutProtocol,
                $"Type '{currentOwnerType.Name}' defines '{routineInfo.Name}' but does not follow '{requiredProtocol}'. " +
                $"Add 'obeys {requiredProtocol}' to the type declaration.",
                location);
        }
    }

    /// <summary>
    /// Checks if a type explicitly declares obeying a protocol (not structural conformance).
    /// This is required for operator methods - you must explicitly declare "obeys Protocol".
    /// </summary>
    private bool ExplicitlyFollowsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the list of explicitly declared protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            ResidentTypeInfo resident => resident.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return false;
        }

        // Check if the protocol is directly declared
        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented.Name == protocolName ||
                GetBaseTypeName(typeName: implemented.Name) == protocolName)
            {
                return true;
            }

            // Check parent protocols recursively (if you follow a protocol that extends the target, that counts)
            if (implemented is ProtocolTypeInfo proto &&
                CheckParentProtocols(proto: proto, targetName: protocolName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves parameters for an external declaration.
    /// </summary>
    private void ResolveExternalParameters(ExternalDeclaration externalDecl)
    {
        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: externalDecl.Name);
        if (routineInfo == null)
        {
            return;
        }

        var parameters = new List<ParameterInfo>();

        foreach (Parameter param in externalDecl.Parameters)
        {
            TypeSymbol paramType = param.Type != null
                ? ResolveType(typeExpr: param.Type)
                : ErrorTypeInfo.Instance;

            parameters.Add(item: new ParameterInfo(name: param.Name, type: paramType)
            {
                DefaultValue = param.DefaultValue
            });
        }

        // Resolve return type
        TypeSymbol? returnType = externalDecl.ReturnType != null
            ? ResolveType(typeExpr: externalDecl.ReturnType)
            : null;

        // Update the routine info with resolved parameters
        _registry.UpdateRoutine(routine: routineInfo,
            parameters: parameters,
            returnType: returnType,
            genericParameters: null,
            genericConstraints: null);
    }

    #endregion

    #region Phase 2.55: Auto-Register Builder-Generated Member Routines

    /// <summary>
    /// Auto-registers builder-generated member routine signatures for all user types.
    /// These are default routines that every type of a given category gets (Text(), to_debug(), hash(), etc.).
    /// Only registers if the user hasn't already defined the routine.
    /// </summary>
    private void AutoRegisterBuiltinRoutines()
    {
        // Look up required types (bail on each if not available)
        TypeSymbol? textType = _registry.LookupType(name: "Text");
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        TypeSymbol? u64Type = _registry.LookupType(name: "U64");
        TypeSymbol? s64Type = _registry.LookupType(name: "S64");

        // Look up protocols for auto-conformance
        TypeSymbol? hashableProtocol = _registry.LookupType(name: "Hashable");

        // Look up List[T] for list-returning synthesized routines
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        TypeSymbol? listTextType = listDef != null && textType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [textType])
            : null;

        // Create or look up FieldInfo record type for member_variable_info()
        TypeSymbol? fieldInfoType = _registry.LookupType(name: "FieldInfo");
        if (fieldInfoType == null && textType != null && u64Type != null)
        {
            fieldInfoType = new RecordTypeInfo(name: "FieldInfo")
            {
                MemberVariables =
                [
                    new MemberVariableInfo(name: "name", type: textType) { Index = 0 },
                    new MemberVariableInfo(name: "type_name", type: textType) { Index = 1 },
                    new MemberVariableInfo(name: "visibility", type: u64Type) { Index = 2 },
                    new MemberVariableInfo(name: "index", type: u64Type) { Index = 3 }
                ],
                Visibility = VisibilityModifier.Open,
                Module = "Core"
            };
            _registry.RegisterType(type: fieldInfoType);
        }

        TypeSymbol? listFieldInfoType = listDef != null && fieldInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [fieldInfoType])
            : null;

        foreach (TypeSymbol type in _registry.GetTypesWithMethods())
        {
            List<RoutineInfo> existingMethods = _registry.GetMethodsForType(type: type)
                                                         .ToList();

            // All types: Text(), to_debug()
            if (textType != null)
            {
                MaybeRegisterBuiltin(owner: type,
                    name: "Text",
                    returnType: textType,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "to_debug",
                    returnType: textType,
                    existingMethods: existingMethods);
            }

            // All types: BuilderService metadata routines
            if (textType != null)
            {
                MaybeRegisterBuiltin(owner: type,
                    name: "type_name",
                    returnType: textType,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "module_name",
                    returnType: textType,
                    existingMethods: existingMethods);
            }

            if (u64Type != null)
            {
                MaybeRegisterBuiltin(owner: type,
                    name: "type_kind",
                    returnType: u64Type,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "type_id",
                    returnType: u64Type,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "field_count",
                    returnType: u64Type,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "data_size",
                    returnType: u64Type,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "align_size",
                    returnType: u64Type,
                    existingMethods: existingMethods);
            }

            if (boolType != null)
            {
                MaybeRegisterBuiltin(owner: type,
                    name: "is_generic",
                    returnType: boolType,
                    existingMethods: existingMethods);
            }

            if (listFieldInfoType != null)
            {
                MaybeRegisterBuiltin(owner: type,
                    name: "member_variable_info",
                    returnType: listFieldInfoType,
                    existingMethods: existingMethods);
            }

            if (listTextType != null)
            {
                MaybeRegisterBuiltin(owner: type,
                    name: "protocols",
                    returnType: listTextType,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "routine_names",
                    returnType: listTextType,
                    existingMethods: existingMethods);
                MaybeRegisterBuiltin(owner: type,
                    name: "annotations",
                    returnType: listTextType,
                    existingMethods: existingMethods);
            }

            switch (type.Category)
            {
                case TypeCategory.Record:
                    if (u64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "hash",
                            returnType: u64Type,
                            existingMethods: existingMethods);
                    if (boolType != null)
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__eq__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);

                    // #27: Records with all-Hashable fields auto-add Hashable conformance
                    if (hashableProtocol != null && type is RecordTypeInfo record)
                    {
                        if (record.ImplementedProtocols.All(p => p.Name != "Hashable") &&
                            AllFieldsHashable(record: record))
                        {
                            var protocols = record.ImplementedProtocols.ToList();
                            protocols.Add(item: hashableProtocol);
                            _registry.UpdateRecordProtocols(recordName: type.FullName,
                                protocols: protocols);
                        }
                    }

                    break;

                case TypeCategory.Entity:
                    if (s64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "id",
                            returnType: s64Type,
                            existingMethods: existingMethods);
                    if (boolType != null)
                    {
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__eq__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__same__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__notsame__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                    }

                    MaybeRegisterBuiltinFailable(owner: type,
                        name: "copy!",
                        returnType: type,
                        existingMethods: existingMethods);
                    break;

                case TypeCategory.Resident:
                    if (s64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "id",
                            returnType: s64Type,
                            existingMethods: existingMethods);
                    if (boolType != null)
                    {
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__eq__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__same__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                        MaybeRegisterBuiltinWithParam(owner: type,
                            name: "__notsame__",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                    }

                    break;

                case TypeCategory.Choice:
                    if (u64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "hash",
                            returnType: u64Type,
                            existingMethods: existingMethods);
                    if (s64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "S64",
                            returnType: s64Type,
                            existingMethods: existingMethods);
                    if (textType != null)
                        MaybeRegisterBuiltinFailable(owner: type,
                            name: "__create__!",
                            returnType: type,
                            existingMethods: existingMethods,
                            param: ("from", textType),
                            kind: RoutineKind.Creator);
                    if (listDef != null)
                    {
                        TypeSymbol listMeType = _registry.GetOrCreateResolution(
                            genericDef: listDef,
                            typeArguments: [type]);
                        MaybeRegisterBuiltin(owner: type,
                            name: "all_cases",
                            returnType: listMeType,
                            existingMethods: existingMethods);
                    }

                    break;

                case TypeCategory.Flags:
                    if (u64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "hash",
                            returnType: u64Type,
                            existingMethods: existingMethods);
                    if (u64Type != null)
                        MaybeRegisterBuiltin(owner: type,
                            name: "U64",
                            returnType: u64Type,
                            existingMethods: existingMethods);
                    MaybeRegisterBuiltin(owner: type,
                        name: "all_on",
                        returnType: type,
                        existingMethods: existingMethods);
                    MaybeRegisterBuiltin(owner: type,
                        name: "all_off",
                        returnType: type,
                        existingMethods: existingMethods);
                    if (listDef != null)
                    {
                        TypeSymbol listMeType = _registry.GetOrCreateResolution(
                            genericDef: listDef,
                            typeArguments: [type]);
                        MaybeRegisterBuiltin(owner: type,
                            name: "all_cases",
                            returnType: listMeType,
                            existingMethods: existingMethods);
                    }

                    break;
            }
        }

        // Auto-register Text.__create__(from: T) for all concrete user types
        // This makes every type structurally satisfy Representable[T]
        if (textType != null)
        {
            List<RoutineInfo> textCreateMethods = _registry.GetMethodsForType(type: textType)
                                                           .Where(predicate: m =>
                                                                m.Name == "__create__")
                                                           .ToList();

            foreach (TypeSymbol type in _registry.GetAllTypes())
            {
                if (type.Category is not (TypeCategory.Record or TypeCategory.Entity
                    or TypeCategory.Resident or TypeCategory.Choice or TypeCategory.Flags
                    or TypeCategory.Variant))
                    continue;

                bool alreadyDefined = textCreateMethods.Any(predicate: m =>
                    m.Parameters.Count == 1 && m.Parameters[0].Type.FullName == type.FullName);
                if (alreadyDefined) continue;

                _registry.RegisterRoutine(routine: new RoutineInfo(name: "__create__")
                {
                    Kind = RoutineKind.Creator,
                    OwnerType = textType,
                    Parameters = [new ParameterInfo(name: "from", type: type)],
                    ReturnType = textType,
                    IsFailable = false,
                    DeclaredModification = ModificationCategory.Readonly,
                    ModificationCategory = ModificationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }
        }
    }

    /// <summary>
    /// Registers a no-parameter readonly builtin routine if not already defined.
    /// </summary>
    private void MaybeRegisterBuiltin(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMethods)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
            return;

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a single-parameter readonly builtin routine if not already defined.
    /// </summary>
    private void MaybeRegisterBuiltinWithParam(TypeSymbol owner, string name, string paramName,
        TypeSymbol paramType, TypeSymbol returnType, List<RoutineInfo> existingMethods)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
            return;

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [new ParameterInfo(name: paramName, type: paramType)],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a failable builtin routine if not already defined (for copy!, __create__!).
    /// </summary>
    private void MaybeRegisterBuiltinFailable(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMethods, (string name, TypeSymbol type)? param = null,
        RoutineKind kind = RoutineKind.MemberRoutine)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
            return;

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = kind,
            OwnerType = owner,
            Parameters = param.HasValue
                ? [new ParameterInfo(name: param.Value.name, type: param.Value.type)]
                : [],
            ReturnType = returnType,
            IsFailable = true,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Checks whether all member variables of a record implement Hashable.
    /// Primitives (S32, U64, Text, Bool, etc.), choices, flags, and other records
    /// that are Hashable are considered hashable. Entities, variants, and
    /// generic parameters are not.
    /// </summary>
    private bool AllFieldsHashable(RecordTypeInfo record)
    {
        IReadOnlyList<MemberVariableInfo> fields = record.MemberVariables;
        if (fields.Count == 0) return true; // empty records are trivially hashable

        foreach (MemberVariableInfo field in fields)
        {
            TypeSymbol fieldType = field.Type;

            // Intrinsic types (primitives) are always hashable
            if (fieldType is IntrinsicTypeInfo) continue;

            // Choices and flags are always hashable
            if (fieldType is ChoiceTypeInfo or FlagsTypeInfo) continue;

            // Records are hashable if they implement Hashable (recursive)
            if (fieldType is RecordTypeInfo fieldRecord)
            {
                if (fieldRecord.ImplementedProtocols.Any(p => p.Name == "Hashable"))
                    continue;
                // Check structurally: does it have hash()?
                if (_registry.LookupMethod(type: fieldType, methodName: "hash") != null)
                    continue;
                return false;
            }

            // Entities, variants, generic parameters, etc. — not hashable
            return false;
        }

        return true;
    }

    #endregion

    #region Phase 2.6: Derived Operator Generation

    /// <summary>
    /// Generates derived comparison operators from __eq__ and __cmp__ routines.
    /// </summary>
    private void GenerateDerivedOperators()
    {
        foreach (TypeSymbol type in _registry.GetTypesWithMethods())
        {
            GenerateDerivedOperatorsForType(type: type);
        }
    }

    /// <summary>
    /// Generates derived operators for a specific type.
    /// </summary>
    /// <param name="type">The type to generate operators for.</param>
    private void GenerateDerivedOperatorsForType(TypeSymbol type)
    {
        IEnumerable<RoutineInfo> methods = _registry.GetMethodsForType(type: type);
        List<RoutineInfo> methodList = methods.ToList();

        // Look for __eq__ method
        RoutineInfo? eqMethod = methodList.FirstOrDefault(predicate: m => m.Name == "__eq__");
        if (eqMethod != null)
        {
            GenerateNeFromEq(type: type, eqMethod: eqMethod, existingMethods: methodList);
        }

        // Look for __cmp__ method
        RoutineInfo? cmpMethod = methodList.FirstOrDefault(predicate: m => m.Name == "__cmp__");
        if (cmpMethod != null)
        {
            GenerateComparisonOperatorsFromCmp(type: type,
                cmpMethod: cmpMethod,
                existingMethods: methodList);
        }
    }

    /// <summary>
    /// Generates __ne__ from __eq__.
    /// __ne__(you) = not __eq__(you)
    /// </summary>
    private void GenerateNeFromEq(TypeSymbol type, RoutineInfo eqMethod,
        List<RoutineInfo> existingMethods)
    {
        RoutineInfo? existingNe =
            existingMethods.FirstOrDefault(predicate: m => m.Name == "__ne__");

        if (existingNe != null)
        {
            // User provided their own implementation — it takes priority over generated.
            // This is expected behavior for @generated protocol routines (#179).
            return;
        }

        // Generate __ne__
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return; // Bool type not available
        }

        var neMethod = new RoutineInfo(name: "__ne__")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = eqMethod.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = eqMethod.Visibility,
            Location = eqMethod.Location,
            Module = eqMethod.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: neMethod);
    }

    /// <summary>
    /// Generates __lt__, __le__, __gt__, __ge__ from __cmp__.
    /// __lt__(you) = __cmp__(you) is ME_SMALL
    /// __le__(you) = __cmp__(you) isnot ME_LARGE
    /// __gt__(you) = __cmp__(you) is ME_LARGE
    /// __ge__(you) = __cmp__(you) isnot ME_SMALL
    /// </summary>
    private void GenerateComparisonOperatorsFromCmp(TypeSymbol type, RoutineInfo cmpMethod,
        List<RoutineInfo> existingMethods)
    {
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return; // Bool type not available
        }

        // Define the derived operators
        var derivedOps = new[]
        {
            ("__lt__", "ME_SMALL", true), // is ME_SMALL
            ("__le__", "ME_LARGE", false), // isnot ME_LARGE
            ("__gt__", "ME_LARGE", true), // is ME_LARGE
            ("__ge__", "ME_SMALL", false) // isnot ME_SMALL
        };

        foreach ((string opName, string _, bool _) in derivedOps)
        {
            RoutineInfo? existing =
                existingMethods.FirstOrDefault(predicate: m => m.Name == opName);

            if (existing != null)
            {
                // User provided their own implementation — it takes priority over generated.
                // This is expected behavior for @generated protocol routines (#179).
                continue;
            }

            // Generate the derived operator
            var derivedMethod = new RoutineInfo(name: opName)
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = type,
                Parameters = cmpMethod.Parameters,
                ReturnType = boolType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = cmpMethod.Visibility,
                Location = cmpMethod.Location,
                Module = cmpMethod.Module,
                Annotations = ["readonly"],
                IsSynthesized = true
            };

            _registry.RegisterRoutine(routine: derivedMethod);
        }
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
        if (type.Location == null || string.IsNullOrEmpty(type.Location.FileName))
        {
            return;
        }

        // Get the list of implemented protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            ResidentTypeInfo resident => resident.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return;
        }

        // Check each protocol
        foreach (TypeSymbol protocol in implementedProtocols)
        {
            if (protocol is ProtocolTypeInfo protoInfo)
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

            // Look for the method on the type
            RoutineInfo? typeMethod =
                _registry.LookupMethod(type: type, methodName: requiredMethod.Name);
            if (typeMethod == null)
            {
                // Also check with failable suffix
                if (requiredMethod.IsFailable)
                {
                    typeMethod = _registry.LookupMethod(type: type,
                        methodName: requiredMethod.Name + "!");
                }
            }

            if (typeMethod == null)
            {
                ReportError(SemanticDiagnosticCode.MissingProtocolMethod,
                    $"Type '{type.Name}' declares 'obeys {protocol.Name}' but does not implement required method '{requiredMethod.Name}'.",
                    type.Location ?? new SourceLocation(FileName: "",
                        Line: 0,
                        Column: 0,
                        Position: 0));
            }
            else if (requiredMethod.GenerationKind == ProtocolRoutineKind.Innate &&
                     !typeMethod.IsSynthesized)
            {
                ReportError(SemanticDiagnosticCode.InnateOverrideNotAllowed,
                    $"Cannot override innate routine '{protocol.Name}.{requiredMethod.Name}'. " +
                    "Innate routines are compiler-provided and cannot be overridden.",
                    typeMethod.Location);
            }
            else if (typeMethod != null)
            {
                // #61: Protocol mutation contract validation
                // Protocol @readonly -> impl must be @readonly
                // Protocol @writable -> impl must be @readonly or @writable (not @migratable)
                if (requiredMethod.Modification == ModificationCategory.Readonly &&
                    typeMethod.ModificationCategory != ModificationCategory.Readonly)
                {
                    ReportError(SemanticDiagnosticCode.ProtocolMutationContractViolation,
                        $"Protocol '{protocol.Name}' requires '{requiredMethod.Name}' to be @readonly, " +
                        $"but implementation on '{type.Name}' is @{typeMethod.ModificationCategory.ToString().ToLowerInvariant()}.",
                        typeMethod.Location);
                }
                else if (requiredMethod.Modification == ModificationCategory.Writable &&
                         typeMethod.ModificationCategory == ModificationCategory.Migratable)
                {
                    ReportError(SemanticDiagnosticCode.ProtocolMutationContractViolation,
                        $"Protocol '{protocol.Name}' requires '{requiredMethod.Name}' to be at most @writable, " +
                        $"but implementation on '{type.Name}' is @migratable.",
                        typeMethod.Location);
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
            if (!validParams.Contains(constraint.ParameterName))
            {
                ReportError(SemanticDiagnosticCode.UnknownTypeParameterInConstraint,
                    $"Type parameter '{constraint.ParameterName}' in constraint is not declared. " +
                    $"Declared type parameters: {(typeParameters?.Count > 0 ? string.Join(", ", typeParameters) : "none")}.",
                    constraint.Location ?? location);
            }
        }
    }

    #endregion
}
