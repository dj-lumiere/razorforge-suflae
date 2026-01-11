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

            case MutantDeclaration mutant:
                CollectMutantDeclaration(mutant: mutant);
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

            case NamespaceDeclaration ns:
                ValidateNamespaceDeclaration(ns: ns);
                break;

            case ImportDeclaration import:
                ProcessImportDeclaration(import: import);
                break;
        }
    }

    /// <summary>
    /// Validates a namespace declaration.
    /// Rejects "namespace Core" as it's reserved for stdlib.
    /// </summary>
    private void ValidateNamespaceDeclaration(NamespaceDeclaration ns)
    {
        // Namespace "Core" is reserved for stdlib only
        if (ns.Path.Equals("Core", StringComparison.OrdinalIgnoreCase))
        {
            ReportError(
                SemanticDiagnosticCode.ReservedNamespaceCore,
                "Namespace 'Core' is reserved for the standard library and cannot be used in user code.",
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
            location: import.Location);

        if (!success)
        {
            ReportError(
                SemanticDiagnosticCode.ModuleNotFound,
                $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                import.Location);
        }
    }

    private void CollectFieldDeclaration(VariableDeclaration field)
    {
        // Fields are VariableDeclarations within type members
        // Validate that the field type is not an inline-only token type

        // Validate getter/setter visibility combinations
        ValidateGetterSetterVisibility(
            getterVisibility: field.Visibility,
            setterVisibility: field.SetterVisibility,
            fieldName: field.Name,
            location: field.Location);

        if (field.Type == null)
        {
            return; // Type inference will be handled later
        }

        TypeSymbol fieldType = ResolveType(typeExpr: field.Type);

        // Validate that tokens cannot be stored in fields
        ValidateNotTokenFieldType(type: fieldType, fieldName: field.Name, location: field.Location);

        // TODO: Register field in the current type's field list when type body resolution is implemented
    }

    private void CollectRecordDeclaration(RecordDeclaration record)
    {
        var typeInfo = new RecordTypeInfo(name: record.Name)
        {
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            Visibility = record.Visibility,
            Location = record.Location,
            Namespace = GetCurrentNamespace()
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
            Namespace = GetCurrentNamespace()
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
            Namespace = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: resident.Location);
    }

    private void CollectChoiceDeclaration(ChoiceDeclaration choice)
    {
        var typeInfo = new ChoiceTypeInfo(name: choice.Name)
        {
            Visibility = choice.Visibility,
            Location = choice.Location,
            Namespace = GetCurrentNamespace()
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
            Namespace = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: variant.Location);
    }

    private void CollectMutantDeclaration(MutantDeclaration mutant)
    {
        var typeInfo = new MutantTypeInfo(name: mutant.Name)
        {
            GenericParameters = mutant.GenericParameters,
            GenericConstraints = mutant.GenericConstraints,
            Location = mutant.Location,
            Namespace = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: mutant.Location);
    }

    private void CollectProtocolDeclaration(ProtocolDeclaration protocol)
    {
        var typeInfo = new ProtocolTypeInfo(name: protocol.Name)
        {
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints,
            Visibility = protocol.Visibility,
            Location = protocol.Location,
            Namespace = GetCurrentNamespace()
        };

        TryRegisterType(type: typeInfo, location: protocol.Location);
    }

    private void CollectFunctionDeclaration(RoutineDeclaration routine)
    {
        RoutineKind kind = _currentType != null
            ? (routine.Name == "__create__" ? RoutineKind.Constructor : RoutineKind.Method)
            : RoutineKind.Function;

        // The AST already stores names without the '!' suffix
        // (e.g., "get!" is parsed as Name="get", IsFailable=true)
        var routineInfo = new RoutineInfo(name: routine.Name)
        {
            Kind = kind,
            OwnerType = _currentType,
            IsFailable = routine.IsFailable,
            GenericParameters = routine.GenericParameters,
            GenericConstraints = routine.GenericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Namespace = GetCurrentNamespace()
        };

        _registry.RegisterRoutine(routine: routineInfo);
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
            Namespace = GetCurrentNamespace()
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

            case MutantDeclaration mutant:
                ResolveMutantBody(mutant: mutant);
                break;
        }
    }

    private void ResolveRecordBody(RecordDeclaration record)
    {
        TypeSymbol? previousType = _currentType;
        _currentType = _registry.LookupType(name: record.Name);

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
            _registry.UpdateRecordProtocols(recordName: record.Name, protocols: resolvedProtocols);
        }

        foreach (Declaration member in record.Members)
        {
            CollectDeclaration(node: member);
        }

        _currentType = previousType;
    }

    private void ResolveEntityBody(EntityDeclaration entity)
    {
        TypeSymbol? previousType = _currentType;
        _currentType = _registry.LookupType(name: entity.Name);

        foreach (Declaration member in entity.Members)
        {
            CollectDeclaration(node: member);
        }

        _currentType = previousType;
    }

    private void ResolveResidentBody(ResidentDeclaration resident)
    {
        TypeSymbol? previousType = _currentType;
        _currentType = _registry.LookupType(name: resident.Name);

        foreach (Declaration member in resident.Members)
        {
            CollectDeclaration(node: member);
        }

        _currentType = previousType;
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

                TypeSymbol paramType = ResolveType(typeExpr: param.Type);
                paramTypes.Add(item: paramType);
                paramNames.Add(item: param.Name);
            }

            // Resolve return type
            TypeSymbol? returnType = sig.ReturnType != null
                ? ResolveType(typeExpr: sig.ReturnType)
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
            Namespace = protocolInfo.Namespace
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

    private void ResolveMutantBody(MutantDeclaration mutant)
    {
        // Track seen types for uniqueness validation
        var seenTypes = new Dictionary<string, string>(); // type name -> case name

        foreach (VariantCase mutantCase in mutant.Cases)
        {
            // Rule 1: All mutant cases must have an associated type
            if (mutantCase.AssociatedTypes == null)
            {
                ReportError(
                    SemanticDiagnosticCode.MutantCaseRequiresType,
                    $"Mutant case '{mutantCase.Name}' must have an associated type. " +
                             "Empty cases are not allowed in mutants.",
                    mutantCase.Location);
                continue;
            }

            TypeSymbol payloadType = ResolveType(typeExpr: mutantCase.AssociatedTypes);

            // Skip further validation if type resolution failed
            if (payloadType is ErrorTypeInfo)
            {
                continue;
            }

            // Rule 2: All types must be unique within the mutant
            string typeName = payloadType.Name;
            if (seenTypes.TryGetValue(key: typeName, value: out string? existingCase))
            {
                ReportError(
                    SemanticDiagnosticCode.MutantCaseTypeDuplicate,
                    $"Mutant case '{mutantCase.Name}' has type '{typeName}' which is already " +
                             $"used by case '{existingCase}'. All mutant case types must be unique.",
                    mutantCase.Location);
            }
            else
            {
                seenTypes[key: typeName] = mutantCase.Name;
            }

            // Validate that tokens cannot be used as mutant payloads
            ValidateNotTokenVariantPayload(
                type: payloadType,
                caseName: mutantCase.Name,
                location: mutantCase.Location);
        }
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
                    $"Cannot define '__ne__' when '__eq__' is defined. " +
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
            Namespace = eqMethod.Namespace,
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
                Namespace = cmpMethod.Namespace,
                Attributes = ["readonly"],
                IsSynthesized = true
            };

            _registry.RegisterRoutine(routine: derivedMethod);
        }
    }

    #endregion
}
