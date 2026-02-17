namespace Compilers.Analysis;

using Enums;
using Symbols;
using Types;
using Shared.AST;
using global::RazorForge.Diagnostics;
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

            case VariantDeclaration variant:
                CollectVariantDeclaration(variant: variant);
                break;

            case ProtocolDeclaration protocol:
                CollectProtocolDeclaration(protocol: protocol);
                break;

            case RoutineDeclaration func:
                CollectFunctionDeclaration(routine: func);
                break;

            case ImportedDeclaration imported:
                CollectImportedDeclaration(imported: imported);
                break;

            case VariableDeclaration variable:
                CollectFieldDeclaration(field: variable);
                break;

            case ModuleDeclaration ns:
                ValidateNamespaceDeclaration(ns: ns);
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
    private void ValidateNamespaceDeclaration(ModuleDeclaration ns)
    {
        // Module "Core" is reserved for stdlib only
        if (ns.Path.Equals("Core", StringComparison.OrdinalIgnoreCase) && !IsStdlibFile(_currentFilePath))
        {
            ReportError(
                SemanticDiagnosticCode.ReservedNamespaceCore,
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
        bool success = _registry.LoadModule(
            importPath: import.ModulePath,
            currentFile: _currentFilePath,
            location: import.Location,
            out string? effectiveNamespace);

        if (!success)
        {
            ReportError(
                SemanticDiagnosticCode.ModuleNotFound,
                $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                import.Location);
            return;
        }

        // Track the imported module for per-file type resolution
        if (effectiveNamespace != null)
        {
            _importedNamespaces.Add(effectiveNamespace);
        }
    }

    private void CollectFieldDeclaration(VariableDeclaration field)
    {
        // Fields are VariableDeclarations within type members
        // Visibility is validated using the simplified four-level system:
        // - public: read/write from anywhere
        // - published: public read, private write
        // - internal: read/write within module
        // - private: read/write within file

        // Check for duplicate field names within the same type
        if (_currentTypeFieldNames != null)
        {
            if (!_currentTypeFieldNames.Add(item: field.Name))
            {
                ReportError(
                    SemanticDiagnosticCode.DuplicateFieldDefinition,
                    $"Field '{field.Name}' is already defined in this type.",
                    field.Location);
            }
        }

        if (field.Type == null)
        {
            return; // Type inference will be handled later
        }

        TypeSymbol fieldType = ResolveType(typeExpr: field.Type);

        // Validate that tokens cannot be stored in fields
        ValidateNotTokenFieldType(type: fieldType, fieldName: field.Name, location: field.Location);

        // Validate that variant types cannot be stored in fields
        if (fieldType is VariantTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.VariantFieldNotAllowed,
                $"Variant type '{fieldType.Name}' cannot be stored in field '{field.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                field.Location);
        }

        // Validate that Result<T> and Lookup<T> are not used as field types
        if (fieldType is ErrorHandlingTypeInfo errorHandlingType &&
            errorHandlingType.Kind is ErrorHandlingKind.Result or ErrorHandlingKind.Lookup)
        {
            ReportError(
                SemanticDiagnosticCode.ErrorHandlingTypeAsField,
                $"'{errorHandlingType.Kind}<T>' cannot be used as a field type. " +
                "Error handling types are internal for error propagation and should not be stored.",
                field.Location);
        }

        // TODO: Register field in the current type's field list when type body resolution is implemented
    }

    private void CollectPresetDeclaration(PresetDeclaration preset)
    {
        TypeSymbol presetType = ResolveType(typeExpr: preset.Type);
        _registry.DeclareVariable(name: preset.Name, type: presetType, isMutable: false, isPreset: true);
    }

    private void CollectRecordDeclaration(RecordDeclaration record)
    {
        var typeInfo = new RecordTypeInfo(name: record.Name)
        {
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            Visibility = record.Visibility,
            Location = record.Location,
            Module = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: record.Location);
    }

    private void CollectEntityDeclaration(EntityDeclaration entity)
    {
        var typeInfo = new EntityTypeInfo(name: entity.Name)
        {
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints,
            Visibility = entity.Visibility,
            Location = entity.Location,
            Module = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: entity.Location);
    }

    private void CollectResidentDeclaration(ResidentDeclaration resident)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                SemanticDiagnosticCode.FeatureNotInSuflae,
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
            Module = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: resident.Location);
    }

    private void CollectChoiceDeclaration(ChoiceDeclaration choice)
    {
        var typeInfo = new ChoiceTypeInfo(name: choice.Name)
        {
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: choice.Location);
    }

    private void CollectVariantDeclaration(VariantDeclaration variant)
    {
        var typeInfo = new VariantTypeInfo(name: variant.Name)
        {
            GenericParameters = variant.GenericParameters,
            GenericConstraints = variant.GenericConstraints,
            Location = variant.Location,
            Module = GetCurrentNamespace()
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
            Module = GetCurrentNamespace()
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
            kind = routine.Name == "__create__" ? RoutineKind.Constructor : RoutineKind.Method;
        }
        else if (routine.Name.Contains(value: '.'))
        {
            // Extension method syntax: "Type.method"
            // Extract type name and method name separately
            int dotIndex = routine.Name.IndexOf(value: '.');
            string typeName = routine.Name[..dotIndex];
            routineName = routine.Name[(dotIndex + 1)..]; // Just the method name

            kind = RoutineKind.Method;
            ownerType = LookupTypeWithImports(name: typeName);
        }
        else
        {
            // Top-level function
            kind = RoutineKind.Function;
        }

        // Validate that variants cannot have methods
        if (ownerType is VariantTypeInfo && kind == RoutineKind.Method)
        {
            ReportError(
                SemanticDiagnosticCode.VariantMethodNotAllowed,
                $"Variant type '{ownerType.Name}' cannot have methods. " +
                "Variants only support 'is', 'isnot', and pattern matching with 'when'.",
                routine.Location);
        }

        // Validate reserved prefixes (try_, check_, lookup_) for user functions
        string baseName = routineName.Contains(value: '.')
            ? routineName[(routineName.IndexOf(value: '.') + 1)..]
            : routineName;

        if (IsReservedRoutinePrefix(name: baseName))
        {
            ReportError(
                SemanticDiagnosticCode.ReservedRoutinePrefix,
                $"Routine name '{baseName}' uses a reserved prefix. " +
                         "Prefixes 'try_', 'check_', and 'lookup_' are reserved for auto-generated error handling variants.",
                routine.Location);
        }

        // Validate dunder patterns (__name__) are known operator methods
        if (IsUnknownDunderMethod(name: baseName))
        {
            ReportError(
                SemanticDiagnosticCode.UnknownDunderMethod,
                $"Routine name '{baseName}' uses reserved dunder pattern. " +
                         "Names matching '__name__' are reserved for operator methods.",
                routine.Location);
        }

        // The AST already stores names without the '!' suffix
        // (e.g., "get!" is parsed as Name="get", IsFailable=true)
        MutationCategory declaredMutation = routine.Attributes.Contains(item: "readonly")
            ? MutationCategory.Readonly
            : routine.Attributes.Contains(item: "writable")
                ? MutationCategory.Writable
                : MutationCategory.Migratable;

        var routineInfo = new RoutineInfo(name: routineName)
        {
            Kind = kind,
            OwnerType = ownerType,
            IsFailable = routine.IsFailable,
            GenericParameters = routine.GenericParameters,
            GenericConstraints = routine.GenericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Module = GetCurrentNamespace(),
            Attributes = routine.Attributes,
            DeclaredMutation = declaredMutation,
            MutationCategory = declaredMutation
        };

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
        // Constructor
        "__create__",

        // Arithmetic operators
        "__add__", "__sub__", "__mul__", "__truediv__", "__floordiv__", "__mod__", "__pow__",

        // Wrapping arithmetic
        "__add_wrap__", "__sub_wrap__", "__mul_wrap__", "__pow_wrap__",

        // Saturating arithmetic
        "__add_sat__", "__sub_sat__", "__mul_sat__", "__pow_sat__",

        // Checked arithmetic
        "__add_checked__", "__sub_checked__", "__mul_checked__",
        "__floordiv_checked__", "__mod_checked__", "__pow_checked__",

        // Comparison operators
        "__eq__", "__ne__", "__lt__", "__le__", "__gt__", "__ge__", "__cmp__",

        // Bitwise operators
        "__and__", "__or__", "__xor__",
        "__ashl__", "__ashl_checked__", "__ashr__", "__lshl__", "__lshr__",

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
        "__destroy__"
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

        // Overflow arithmetic (wrapping)
        ["__add_wrap__"] = "OverflowAddable",
        ["__sub_wrap__"] = "OverflowSubtractable",
        ["__mul_wrap__"] = "OverflowMultiplicable",
        ["__pow_wrap__"] = "OverflowExponentiable",

        // Overflow arithmetic (saturating)
        ["__add_sat__"] = "OverflowAddable",
        ["__sub_sat__"] = "OverflowSubtractable",
        ["__mul_sat__"] = "OverflowMultiplicable",
        ["__pow_sat__"] = "OverflowExponentiable",

        // Overflow arithmetic (checked)
        ["__add_checked__"] = "OverflowAddable",
        ["__sub_checked__"] = "OverflowSubtractable",
        ["__mul_checked__"] = "OverflowMultiplicable",
        ["__floordiv_checked__"] = "CheckedDivisible",
        ["__mod_checked__"] = "CheckedDivisible",
        ["__pow_checked__"] = "OverflowExponentiable",

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
        ["__ashl_checked__"] = "CheckedShiftable",

        // Unary operators
        ["__neg__"] = "Negatable",
        ["__not__"] = "Invertible",

        // Container operators
        ["__contains__"] = "Container",
        ["__notcontains__"] = "Container",
        ["__getitem__"] = "Indexable",
        ["__setitem__"] = "Indexable",

        // Sequence operators
        ["__seq__"] = "Sequential",
        ["__try_next__"] = "SequenceGenerator"
    };

    /// <summary>
    /// Gets the required protocol for a dunder method, or null if no protocol is required.
    /// </summary>
    private static string? GetRequiredProtocol(string dunderName)
    {
        return DunderToProtocol.GetValueOrDefault(key: dunderName);
    }

    private void CollectImportedDeclaration(ImportedDeclaration imported)
    {
        var routineInfo = new RoutineInfo(name: imported.Name)
        {
            Kind = RoutineKind.Imported,
            CallingConvention = imported.CallingConvention,
            IsVariadic = imported.IsVariadic,
            Visibility = VisibilityModifier.Public, // Imported declarations are always public
            Location = imported.Location,
            Module = GetCurrentNamespace()
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
            ReportError(
                SemanticDiagnosticCode.DuplicateTypeDefinition,
                $"Type '{type.Name}' is already defined.",
                location);
        }
    }

    #endregion

    #region Phase 2: Type Body Resolution

    /// <summary>
    /// Resolves type bodies including fields and method signatures.
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
        }
    }

    private void ResolveRecordBody(RecordDeclaration record)
    {
        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeFieldNames;

        _currentType = _registry.LookupType(name: record.Name);
        _currentTypeFieldNames = [];

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
                    ReportError(
                        SemanticDiagnosticCode.NotAProtocol,
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'follows'.",
                        protoExpr.Location);
                }
            }

            // Update the type with resolved protocols
            _registry.UpdateRecordProtocols(recordName: _currentType!.FullName, protocols: resolvedProtocols);
        }

        // Validate generic constraints reference declared type parameters
        ValidateConstraintTypeParameters(
            constraints: record.GenericConstraints,
            typeParameters: record.GenericParameters,
            location: record.Location);

        // Collect fields and other members
        var fields = new List<FieldInfo>();
        int fieldIndex = 0;

        foreach (Declaration member in record.Members)
        {
            if (member is VariableDeclaration field)
            {
                // Resolve field type
                TypeSymbol fieldType = field.Type != null
                    ? ResolveType(typeExpr: field.Type)
                    : ErrorTypeInfo.Instance;

                // Records can only contain value types + Snatched<T>
                // Entities, wrappers (Shared, Tracked, Viewed, etc.), and reference tuples are not allowed
                if (fieldType is TypeInfo fieldTypeInfo
                    && fieldTypeInfo is not ErrorTypeInfo
                    && !TypeRegistry.IsValueType(type: fieldTypeInfo)
                    && !(fieldTypeInfo is WrapperTypeInfo { Name: "Snatched" }))
                {
                    ReportError(
                        SemanticDiagnosticCode.RecordContainsNonValueType,
                        $"Record field '{field.Name}' has type '{fieldType.Name}' which is not a value type. " +
                        "Records can only contain value types (records, choices, variants, value tuples) and Snatched<T>.",
                        field.Location);
                }

                // Create field info
                var fieldInfo = new FieldInfo(name: field.Name, type: fieldType)
                {
                    IsMutable = field.IsMutable,
                    Visibility = field.Visibility,
                    Index = fieldIndex++,
                    HasDefaultValue = field.Initializer != null,
                    Location = field.Location,
                    Owner = _currentType
                };

                fields.Add(item: fieldInfo);
            }

            // Still call CollectDeclaration for validation and other member types
            CollectDeclaration(node: member);
        }

        // Update the record with resolved fields
        if (fields.Count > 0)
        {
            _registry.UpdateRecordFields(recordName: _currentType!.FullName, fields: fields);
        }

        _currentType = previousType;
        _currentTypeFieldNames = previousFieldNames;
    }

    private void ResolveEntityBody(EntityDeclaration entity)
    {
        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeFieldNames;

        _currentType = _registry.LookupType(name: entity.Name);
        _currentTypeFieldNames = [];

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
                    ReportError(
                        SemanticDiagnosticCode.NotAProtocol,
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'follows'.",
                        protoExpr.Location);
                }
            }

            _registry.UpdateEntityProtocols(entityName: _currentType!.FullName, protocols: resolvedProtocols);
        }

        // Collect fields and other members
        var fields = new List<FieldInfo>();
        int fieldIndex = 0;

        foreach (Declaration member in entity.Members)
        {
            if (member is VariableDeclaration field)
            {
                TypeSymbol fieldType = field.Type != null
                    ? ResolveType(typeExpr: field.Type)
                    : ErrorTypeInfo.Instance;

                var fieldInfo = new FieldInfo(name: field.Name, type: fieldType)
                {
                    IsMutable = field.IsMutable,
                    Visibility = field.Visibility,
                    Index = fieldIndex++,
                    HasDefaultValue = field.Initializer != null,
                    Location = field.Location,
                    Owner = _currentType
                };

                fields.Add(item: fieldInfo);
            }

            CollectDeclaration(node: member);
        }

        if (fields.Count > 0)
        {
            _registry.UpdateEntityFields(entityName: _currentType!.FullName, fields: fields);
        }

        _currentType = previousType;
        _currentTypeFieldNames = previousFieldNames;
    }

    private void ResolveResidentBody(ResidentDeclaration resident)
    {
        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeFieldNames;

        _currentType = _registry.LookupType(name: resident.Name);
        _currentTypeFieldNames = [];

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
                }
                else if (protoType is not ErrorTypeInfo)
                {
                    ReportError(
                        SemanticDiagnosticCode.NotAProtocol,
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'follows'.",
                        protoExpr.Location);
                }
            }

            _registry.UpdateResidentProtocols(residentName: _currentType!.FullName, protocols: resolvedProtocols);
        }

        // Collect fields and other members
        var fields = new List<FieldInfo>();
        int fieldIndex = 0;

        foreach (Declaration member in resident.Members)
        {
            if (member is VariableDeclaration field)
            {
                TypeSymbol fieldType = field.Type != null
                    ? ResolveType(typeExpr: field.Type)
                    : ErrorTypeInfo.Instance;

                var fieldInfo = new FieldInfo(name: field.Name, type: fieldType)
                {
                    IsMutable = field.IsMutable,
                    Visibility = field.Visibility,
                    Index = fieldIndex++,
                    HasDefaultValue = field.Initializer != null,
                    Location = field.Location,
                    Owner = _currentType
                };

                fields.Add(item: fieldInfo);
            }

            CollectDeclaration(node: member);
        }

        if (fields.Count > 0)
        {
            _registry.UpdateResidentFields(residentName: _currentType!.FullName, fields: fields);
        }

        _currentType = previousType;
        _currentTypeFieldNames = previousFieldNames;
    }

    private void ResolveProtocolBody(ProtocolDeclaration protocol)
    {
        // Look up the registered protocol type
        TypeSymbol? protoType = _registry.LookupType(name: protocol.Name);
        if (protoType is not ProtocolTypeInfo protocolInfo)
        {
            return;
        }

        // Resolve parent protocols (protocol X follows Y, Z)
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
                ReportError(
                    SemanticDiagnosticCode.NotAProtocol,
                    $"'{parentExpr}' is not a protocol. Only protocols can be inherited with 'follows'.",
                    parentExpr.Location);
            }
        }

        // Convert method signatures to ProtocolMethodInfo
        var methods = new List<ProtocolMethodInfo>();
        foreach (RoutineSignature sig in protocol.Methods)
        {
            bool isFailable = sig.Name.EndsWith(value: '!');
            string fullName = isFailable ? sig.Name[..^1] : sig.Name;

            // Check if this is an instance method (has "Me." prefix)
            // Protocol methods: "Me.methodName" = instance, "methodName" = type-level
            bool isInstanceMethod = fullName.StartsWith(value: "Me.");
            string methodName = isInstanceMethod ? fullName[3..] : fullName;

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

            // Extract mutation category from attributes
            // @readonly -> Readonly, @writable -> Writable, default/no annotation -> Migratable
            MutationCategory mutation = MutationCategory.Migratable; // Default
            if (sig.Attributes != null)
            {
                if (sig.Attributes.Contains(item: "readonly"))
                {
                    mutation = MutationCategory.Readonly;
                }
                else if (sig.Attributes.Contains(item: "writable"))
                {
                    mutation = MutationCategory.Writable;
                }
                // else: "migratable" or no annotation = Migratable (default)
            }

            var methodInfo = new ProtocolMethodInfo(name: methodName)
            {
                IsInstanceMethod = isInstanceMethod,
                Mutation = mutation,
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
        // Validate each variant case's payload type
        foreach (VariantCase variantCase in variant.Cases)
        {
            if (variantCase.AssociatedTypes == null)
            {
                continue; // No payload for this case
            }

            TypeSymbol payloadType = ResolveType(typeExpr: variantCase.AssociatedTypes);

            // Validate that tokens cannot be used as variant payloads
            ValidateNotTokenVariantPayload(
                type: payloadType,
                caseName: variantCase.Name,
                location: variantCase.Location);
        }
    }

    /// <summary>
    /// Resolves choice body, populating the choice cases.
    /// </summary>
    private void ResolveChoiceBody(ChoiceDeclaration choice)
    {
        TypeSymbol? choiceType = _registry.LookupType(name: choice.Name);
        if (choiceType is not ChoiceTypeInfo choiceInfo)
        {
            return;
        }

        var cases = new List<ChoiceCaseInfo>();
        int autoValue = 0;

        foreach (ChoiceCase caseDecl in choice.Cases)
        {
            int? explicitValue = null;

            // Evaluate explicit value if provided
            if (caseDecl.Value != null)
            {
                if (caseDecl.Value is LiteralExpression literal && literal.Value is int intVal)
                {
                    explicitValue = intVal;
                    autoValue = intVal + 1;
                }
                else if (caseDecl.Value is LiteralExpression litExpr && litExpr.Value is long longVal)
                {
                    explicitValue = (int)longVal;
                    autoValue = explicitValue.Value + 1;
                }
                // TODO: Handle other literal types
            }

            int computedValue = explicitValue ?? autoValue++;

            cases.Add(new ChoiceCaseInfo(name: caseDecl.Name)
            {
                Value = explicitValue,
                ComputedValue = computedValue,
                Location = caseDecl.Location
            });
        }

        // Update the choice with resolved cases
        _registry.UpdateChoiceCases(choiceName: choiceInfo.FullName, cases: cases);
    }

    #endregion

    #region Phase 2.5: Routine Signature Resolution

    /// <summary>
    /// Resolves routine signatures including parameter types.
    /// Performs protocol-as-type desugaring (routine foo(x: Displayable) → routine foo&lt;T follows Displayable&gt;(x: T)).
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

            case ImportedDeclaration imported:
                ResolveImportedParameters(imported: imported);
                break;
        }
    }

    /// <summary>
    /// Resolves parameters for a routine declaration, performing protocol-as-type desugaring.
    /// </summary>
    private void ResolveRoutineParameters(RoutineDeclaration routine)
    {
        bool isFailable = routine.Name.EndsWith(value: '!');
        string routineName = isFailable ? routine.Name[..^1] : routine.Name;

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
                // Type inference required - handle later
                parameters.Add(item: new ParameterInfo(name: param.Name, type: ErrorTypeInfo.Instance));
                continue;
            }

            TypeSymbol paramType = ResolveType(typeExpr: param.Type);

            // Validate that variant types cannot be used as parameter types
            if (paramType is VariantTypeInfo)
            {
                ReportError(
                    SemanticDiagnosticCode.VariantParameterNotAllowed,
                    $"Variant type '{paramType.Name}' cannot be used as a parameter type. " +
                    "Return variants from routines and dismantle them with pattern matching.",
                    param.Location);
            }

            // Validate that Result<T> and Lookup<T> are not used as parameter types
            if (paramType is ErrorHandlingTypeInfo errorHandlingType &&
                errorHandlingType.Kind is ErrorHandlingKind.Result or ErrorHandlingKind.Lookup)
            {
                ReportError(
                    SemanticDiagnosticCode.ErrorHandlingTypeAsParameter,
                    $"'{errorHandlingType.Kind}<T>' cannot be used as a parameter type. " +
                    "Error handling types are internal for error propagation and should not be passed as arguments.",
                    param.Location);
            }

            // Protocol-as-type desugaring: routine foo(x: Displayable) → routine foo<T follows Displayable>(x: T)
            if (paramType is ProtocolTypeInfo)
            {
                // Generate implicit generic parameter name
                string implicitGenericName = $"__T{implicitGenericCounter++}";
                implicitGenerics.Add(item: implicitGenericName);

                // Create "follows" constraint for the implicit generic
                var constraint = new GenericConstraintDeclaration(
                    ParameterName: implicitGenericName,
                    ConstraintType: ConstraintKind.Follows,
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

        // Merge implicit generics with explicit generics
        List<string> allGenericParams = routineInfo.GenericParameters?.ToList() ?? [];
        allGenericParams.AddRange(collection: implicitGenerics);

        // Merge implicit constraints with explicit constraints
        List<GenericConstraintDeclaration> allConstraints = routineInfo.GenericConstraints?.ToList() ?? [];
        allConstraints.AddRange(collection: implicitConstraints);

        // Update the routine info with resolved parameters
        _registry.UpdateRoutine(
            routine: routineInfo,
            parameters: parameters,
            returnType: returnType,
            genericParameters: allGenericParams.Count > 0 ? allGenericParams : null,
            genericConstraints: allConstraints.Count > 0 ? allConstraints : null);

        // Re-lookup the updated routine for validation
        RoutineInfo? updatedRoutineInfo = _registry.LookupRoutine(fullName: routineInfo.FullName);
        if (updatedRoutineInfo == null)
        {
            return;
        }

        // Validate operator protocol conformance for dunder methods
        ValidateOperatorProtocolConformance(routineInfo: updatedRoutineInfo, location: routine.Location);

        // Validate that the method matches the protocol signature if the type declares following a protocol
        ValidateProtocolMethodSignature(routineInfo: updatedRoutineInfo, location: routine.Location);
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
            ValidateMethodAgainstProtocol(
                typeMethod: routineInfo,
                protoMethod: protoMethod,
                protocol: protocol,
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type method matches the expected protocol method signature.
    /// Reports specific errors for mismatches.
    /// </summary>
    private void ValidateMethodAgainstProtocol(
        RoutineInfo typeMethod,
        ProtocolMethodInfo protoMethod,
        ProtocolTypeInfo protocol,
        SourceLocation? location)
    {
        // Check failable matches
        if (typeMethod.IsFailable != protoMethod.IsFailable)
        {
            string expected = protoMethod.IsFailable ? "failable (!)" : "non-failable";
            string actual = typeMethod.IsFailable ? "failable (!)" : "non-failable";
            ReportError(
                SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                $"Method '{typeMethod.Name}' should be {expected} to match protocol '{protocol.Name}', but is {actual}.",
                location);
            return;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body methods have explicit 'me' as first parameter
        // Extension methods don't include 'me' in the parameter list
        int expectedParamCount = protoMethod.ParameterTypes.Count;
        bool hasMeParam = typeMethod.Parameters.Count > 0 && typeMethod.Parameters[0].Name == "me";
        int actualParamCount = typeMethod.Parameters.Count - (hasMeParam ? 1 : 0);

        if (actualParamCount != expectedParamCount)
        {
            ReportError(
                SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                $"Method '{typeMethod.Name}' has {actualParamCount} parameter(s) but protocol '{protocol.Name}' expects {expectedParamCount}.",
                location);
            return;
        }

        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam ? 1 : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMethod.ParameterTypes[i];
            TypeSymbol actualType = typeMethod.Parameters[startIndex + i].Type;

            // Handle protocol self type (Me) - should match the owner type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                if (typeMethod.OwnerType != null && actualType.Name != typeMethod.OwnerType.Name)
                {
                    ReportError(
                        SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        $"Parameter '{protoMethod.ParameterNames[i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location);
                }
            }
            else if (actualType.Name != expectedType.Name)
            {
                ReportError(
                    SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
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
                    ReportError(
                        SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location);
                }
            }
            else if (actualReturn.Name != expectedReturn.Name)
            {
                ReportError(
                    SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                    $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{expectedReturn.Name}'.",
                    location);
            }
        }
    }

    /// <summary>
    /// Validates that a type follows the required protocol when defining operator methods.
    /// For example, defining __add__ requires the type to follow Addable.
    /// </summary>
    private void ValidateOperatorProtocolConformance(RoutineInfo routineInfo, SourceLocation? location)
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

        // Check if the owner type EXPLICITLY follows the required protocol
        // (structural conformance doesn't count - you must declare "follows Protocol")
        if (!ExplicitlyFollowsProtocol(type: currentOwnerType, protocolName: requiredProtocol))
        {
            ReportError(
                SemanticDiagnosticCode.OperatorWithoutProtocol,
                $"Type '{currentOwnerType.Name}' defines '{routineInfo.Name}' but does not follow '{requiredProtocol}'. " +
                $"Add 'follows {requiredProtocol}' to the type declaration.",
                location);
        }
    }

    /// <summary>
    /// Checks if a type explicitly declares following a protocol (not structural conformance).
    /// This is required for operator methods - you must explicitly declare "follows Protocol".
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
            if (implemented.Name == protocolName || GetBaseTypeName(typeName: implemented.Name) == protocolName)
            {
                return true;
            }

            // Check parent protocols recursively (if you follow a protocol that extends the target, that counts)
            if (implemented is ProtocolTypeInfo proto && CheckParentProtocols(proto: proto, targetName: protocolName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves parameters for an imported declaration.
    /// </summary>
    private void ResolveImportedParameters(ImportedDeclaration imported)
    {
        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: imported.Name);
        if (routineInfo == null)
        {
            return;
        }

        var parameters = new List<ParameterInfo>();

        foreach (Parameter param in imported.Parameters)
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
        TypeSymbol? returnType = imported.ReturnType != null
            ? ResolveType(typeExpr: imported.ReturnType)
            : null;

        // Update the routine info with resolved parameters
        _registry.UpdateRoutine(
            routine: routineInfo,
            parameters: parameters,
            returnType: returnType,
            genericParameters: null,
            genericConstraints: null);
    }

    #endregion

    #region Phase 2.6: Derived Operator Generation

    /// <summary>
    /// Generates derived comparison operators from __eq__ and __cmp__ methods.
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
            GenerateComparisonOperatorsFromCmp(type: type, cmpMethod: cmpMethod, existingMethods: methodList);
        }
    }

    /// <summary>
    /// Generates __ne__ from __eq__.
    /// __ne__(you) = not __eq__(you)
    /// </summary>
    private void GenerateNeFromEq(TypeSymbol type, RoutineInfo eqMethod, List<RoutineInfo> existingMethods)
    {
        RoutineInfo? existingNe = existingMethods.FirstOrDefault(predicate: m => m.Name == "__ne__");

        if (existingNe != null)
        {
            // User cannot override derived operators
            if (!existingNe.IsSynthesized)
            {
                ReportError(
                    SemanticDiagnosticCode.DerivedOperatorOverride,
                    "Cannot define '__ne__' when '__eq__' is defined. " +
                             "'__ne__' is auto-generated from '__eq__'.",
                    existingNe.Location);
            }

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
            Kind = RoutineKind.Method,
            OwnerType = type,
            Parameters = eqMethod.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = eqMethod.Visibility,
            Location = eqMethod.Location,
            Module = eqMethod.Module,
            Attributes = ["readonly"],
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
            ("__lt__", "ME_SMALL", true),   // is ME_SMALL
            ("__le__", "ME_LARGE", false),  // isnot ME_LARGE
            ("__gt__", "ME_LARGE", true),   // is ME_LARGE
            ("__ge__", "ME_SMALL", false)   // isnot ME_SMALL
        };

        foreach ((string opName, string _, bool _) in derivedOps)
        {
            RoutineInfo? existing = existingMethods.FirstOrDefault(predicate: m => m.Name == opName);

            if (existing != null)
            {
                // User cannot override derived operators
                if (!existing.IsSynthesized)
                {
                    ReportError(
                        SemanticDiagnosticCode.DerivedOperatorOverride,
                        $"Cannot define '{opName}' when '__cmp__' is defined. " +
                                 $"'{opName}' is auto-generated from '__cmp__'.",
                        existing.Location);
                }

                continue;
            }

            // Generate the derived operator
            var derivedMethod = new RoutineInfo(name: opName)
            {
                Kind = RoutineKind.Method,
                OwnerType = type,
                Parameters = cmpMethod.Parameters,
                ReturnType = boolType,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = cmpMethod.Visibility,
                Location = cmpMethod.Location,
                Module = cmpMethod.Module,
                Attributes = ["readonly"],
                IsSynthesized = true
            };

            _registry.RegisterRoutine(routine: derivedMethod);
        }
    }

    #endregion

    #region Phase 2.7: Protocol Implementation Validation

    /// <summary>
    /// Validates that all types declaring "follows Protocol" implement all required protocol methods.
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
            RoutineInfo? typeMethod = _registry.LookupMethod(type: type, methodName: requiredMethod.Name);
            if (typeMethod == null)
            {
                // Also check with failable suffix
                if (requiredMethod.IsFailable)
                {
                    typeMethod = _registry.LookupMethod(type: type, methodName: requiredMethod.Name + "!");
                }
            }

            if (typeMethod == null)
            {
                ReportError(
                    SemanticDiagnosticCode.MissingProtocolMethod,
                    $"Type '{type.Name}' declares 'follows {protocol.Name}' but does not implement required method '{requiredMethod.Name}'.",
                    type.Location ?? new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0));
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
        IReadOnlyList<string>? typeParameters,
        SourceLocation? location)
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
                ReportError(
                    SemanticDiagnosticCode.UnknownTypeParameterInConstraint,
                    $"Type parameter '{constraint.ParameterName}' in constraint is not declared. " +
                    $"Declared type parameters: {(typeParameters?.Count > 0 ? string.Join(", ", typeParameters) : "none")}.",
                    constraint.Location ?? location);
            }
        }
    }

    #endregion
}
