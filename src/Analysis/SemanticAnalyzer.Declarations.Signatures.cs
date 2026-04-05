namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    #region Phase 2.5: Routine Signature Resolution and Registration

    /// <summary>
    /// Resolves routine signatures and registers them in the type registry.
    /// Processes pending routines collected during Phase 1 and Phase 2.
    /// Performs protocol-as-type desugaring and duplicate detection by full signature.
    /// </summary>
    /// <param name="filterFilePath">If set, only processes pending routines from this file.</param>
    private void ResolveAndRegisterPendingRoutines(string? filterFilePath = null)
    {
        List<PendingRoutine> toProcess;
        if (filterFilePath != null)
        {
            toProcess = _pendingRoutines
                       .Where(predicate: p => p.FilePath == filterFilePath)
                       .ToList();
            _pendingRoutines.RemoveAll(match: p => p.FilePath == filterFilePath);
        }
        else
        {
            toProcess = _pendingRoutines.ToList();
            _pendingRoutines.Clear();
        }

        foreach (PendingRoutine pending in toProcess)
        {
            ResolveAndRegisterRoutine(pending: pending);
        }
    }

    /// <summary>
    /// Resolves a single pending routine's signature and registers it.
    /// </summary>
    private void ResolveAndRegisterRoutine(PendingRoutine pending)
    {
        RoutineDeclaration routine = pending.Declaration;

        ModificationCategory declaredModification =
            routine.Annotations.Contains(item: "readonly") ? ModificationCategory.Readonly :
            routine.Annotations.Contains(item: "writable") ? ModificationCategory.Writable :
            ModificationCategory.Migratable;

        // Create preliminary RoutineInfo for generic parameter resolution context.
        // IsGenericParameter() checks _currentRoutine.GenericParameters to know which
        // type names are generic params (e.g., T, U) vs real types.
        var contextRoutine = new RoutineInfo(name: pending.RoutineName)
        {
            Kind = pending.Kind,
            OwnerType = pending.OwnerType,
            GenericParameters = routine.GenericParameters,
            GenericConstraints = routine.GenericConstraints,
            Module = pending.Module,
            IsFailable = routine.IsFailable,
            Location = routine.Location
        };

        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = contextRoutine;

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
                    parameters.Add(item: new ParameterInfo(name: param.Name, type: dataType)
                    {
                        IsVariadicParam = param.IsVariadic
                    });
                }
                else
                {
                    // Type inference required - handle later
                    parameters.Add(item: new ParameterInfo(name: param.Name,
                        type: ErrorTypeInfo.Instance) { IsVariadicParam = param.IsVariadic });
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
                ReportError(code: SemanticDiagnosticCode.VariantParameterNotAllowed,
                    message:
                    $"Variant type '{paramType.Name}' cannot be used as a parameter type. " +
                    "Return variants from routines and dismantle them with pattern matching.",
                    location: param.Location);
            }

            // Validate that Result<T> and Lookup<T> are not used as parameter types
            if (IsCarrierType(type: paramType) && !IsMaybeType(type: paramType))
            {
                string carrierName = GetCarrierBaseName(type: paramType)!;
                ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsParameter,
                    message:
                    $"'{carrierName}[T]' cannot be used as a parameter type. " +
                    "Error handling types are internal for error propagation and should not be passed as arguments.",
                    location: param.Location);
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
                    DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
                });
            }
            else
            {
                parameters.Add(item: new ParameterInfo(name: param.Name, type: paramType)
                {
                    DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
                });
            }
        }

        // Resolve return type
        TypeSymbol? returnType = routine.ReturnType != null
            ? ResolveType(typeExpr: routine.ReturnType)
            : null;

        // Validate that Maybe<T>/Result<T>/Lookup<T> are not used as return types
        // These are builder-generated wrapper types for failable routines (!)
        if (returnType != null && IsCarrierType(type: returnType) &&
            !IsStdlibFile(filePath: _currentFilePath))
        {
            string carrierName = GetCarrierBaseName(type: returnType)!;
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType,
                message: $"Routine cannot return '{carrierName}[T]'. " +
                         "These types are builder-generated for failable routines. " +
                         "Use a failable routine (!) with 'throw'/'absent' instead.",
                location: routine.ReturnType?.Location ?? routine.Location);
        }

        // Merge implicit generics with explicit generics
        List<string> allGenericParams = routine.GenericParameters?.ToList() ?? [];
        allGenericParams.AddRange(collection: implicitGenerics);

        // Merge implicit constraints with explicit constraints
        List<GenericConstraintDeclaration> allConstraints =
            routine.GenericConstraints?.ToList() ?? [];
        allConstraints.AddRange(collection: implicitConstraints);

        _currentRoutine = prevRoutine;

        // Create the final RoutineInfo with fully resolved signature
        var finalRoutine = new RoutineInfo(name: pending.RoutineName)
        {
            Kind = pending.Kind,
            OwnerType = pending.OwnerType,
            Parameters = parameters,
            ReturnType = returnType,
            IsFailable = routine.IsFailable,
            IsVariadic = routine.Parameters.Any(predicate: p => p.IsVariadic),
            GenericParameters = allGenericParams.Count > 0
                ? allGenericParams
                : null,
            GenericConstraints = allConstraints.Count > 0
                ? allConstraints
                : null,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Module = pending.Module,
            ModulePath = pending.Module?.Split('/'),
            Annotations = routine.Annotations,
            DeclaredModification = declaredModification,
            ModificationCategory = declaredModification,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage,
            AsyncStatus = routine.Async
        };

        // Duplicate detection by full signature (RegistryKey includes param types)
        if (_registry.HasRoutine(key: finalRoutine.RegistryKey))
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateRoutineDefinition,
                message: $"Routine '{pending.RoutineName}' is already defined.",
                location: routine.Location);
            return;
        }

        _registry.RegisterRoutine(routine: finalRoutine);

        // Post-registration validation
        ValidateOperatorProtocolConformance(routineInfo: finalRoutine,
            location: routine.Location);
        ValidateProtocolMethodSignature(routineInfo: finalRoutine,
            location: routine.Location);
    }

    /// <summary>
    /// Resolves external routine signatures (parameter types and return types).
    /// Externals are registered in Phase 1 and updated here with resolved types.
    /// </summary>
    private void ResolveExternalSignatures(Program program)
    {
        foreach (IAstNode declaration in program.Declarations)
        {
            switch (declaration)
            {
                case ExternalDeclaration externalDecl:
                    ResolveExternalParameters(externalDecl: externalDecl);
                    break;

                case ExternalBlockDeclaration block:
                    foreach (Declaration decl in block.Declarations)
                    {
                        if (decl is ExternalDeclaration ext)
                        {
                            ResolveExternalParameters(externalDecl: ext);
                        }
                    }

                    break;
            }
        }
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
        TypeSymbol? currentOwnerType = _registry.LookupType(name: routineInfo.OwnerType.FullName);
        if (currentOwnerType == null)
        {
            return;
        }

        // Get the list of implemented protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = currentOwnerType switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
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
        // Build substitution map for generic protocols (e.g., Supplier[S32]: T → S32)
        Dictionary<string, string>? substitution = null;
        if (protocol.TypeArguments is { Count: > 0 })
        {
            ProtocolTypeInfo genericDef = protocol.GenericDefinition ?? protocol;
            if (genericDef.GenericParameters is { Count: > 0 })
            {
                substitution = new Dictionary<string, string>();
                for (int i = 0;
                     i < genericDef.GenericParameters.Count &&
                     i < protocol.TypeArguments.Count;
                     i++)
                {
                    substitution[key: genericDef.GenericParameters[index: i]] =
                        protocol.TypeArguments[index: i].Name;
                }
            }
        }

        // Check failable matches
        if (typeMethod.IsFailable != protoMethod.IsFailable)
        {
            string expected = protoMethod.IsFailable
                ? "failable (!)"
                : "non-failable";
            string actual = typeMethod.IsFailable
                ? "failable (!)"
                : "non-failable";
            ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                message:
                $"Method '{typeMethod.Name}' should be {expected} to match protocol '{protocol.Name}', but is {actual}.",
                location: location);
            return;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body methods have explicit 'me' as first parameter
        // Extension methods don't include 'me' in the parameter list
        int expectedParamCount = protoMethod.ParameterTypes.Count;
        bool hasMeParam = typeMethod.Parameters.Count > 0 &&
                          typeMethod.Parameters[index: 0].Name == "me";
        int actualParamCount = typeMethod.Parameters.Count - (hasMeParam
            ? 1
            : 0);

        if (actualParamCount != expectedParamCount)
        {
            ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                message:
                $"Method '{typeMethod.Name}' has {actualParamCount} parameter(s) but protocol '{protocol.Name}' expects {expectedParamCount}.",
                location: location);
            return;
        }

        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam
            ? 1
            : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMethod.ParameterTypes[index: i];
            TypeSymbol actualType = typeMethod.Parameters[index: startIndex + i].Type;

            // Handle protocol self type (Me) - should match the owner type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                if (typeMethod.OwnerType != null &&
                    !MeTypeMatches(actualType: actualType,
                        ownerType: typeMethod.OwnerType))
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        message:
                        $"Parameter '{protoMethod.ParameterNames[index: i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location: location);
                }
            }
            else
            {
                string expectedName = substitution != null &&
                                      substitution.TryGetValue(key: expectedType.Name,
                                          value: out string? substName)
                    ? substName
                    : expectedType.Name;
                if (actualType.Name != expectedName)
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        message:
                        $"Parameter '{protoMethod.ParameterNames[index: i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{expectedName}'.",
                        location: location);
                }
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
                if (typeMethod.OwnerType != null &&
                    !MeTypeMatches(actualType: actualReturn,
                        ownerType: typeMethod.OwnerType))
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        message:
                        $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location: location);
                }
            }
            else
            {
                string expectedReturnName = substitution != null &&
                                            substitution.TryGetValue(key: expectedReturn.Name,
                                                value: out string? substRetName)
                    ? substRetName
                    : expectedReturn.Name;
                if (actualReturn.Name != expectedReturnName)
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        message:
                        $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{expectedReturnName}'.",
                        location: location);
                }
            }
        }
    }

    /// <summary>
    /// Structural comparison: checks if an actual type matches the owner type for protocol Me type validation.
    /// Handles generic resolutions (e.g., Total[T] matches owner Total).
    /// </summary>
    private static bool MeTypeMatches(TypeSymbol actualType, TypeSymbol ownerType)
    {
        // Direct match
        if (ReferenceEquals(objA: actualType, objB: ownerType) ||
            actualType.Name == ownerType.Name)
        {
            return true;
        }

        // Generic resolution: actual is a generic instance of the owner type definition
        TypeSymbol? actualDef = actualType switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (actualDef != null &&
            (ReferenceEquals(objA: actualDef, objB: ownerType) ||
             actualDef.Name == ownerType.Name))
        {
            return true;
        }

        // Parameterized with own generic params: "Total[T]" matches owner "Total"
        if (ownerType.GenericParameters is { Count: > 0 } &&
            actualType.Name.StartsWith(value: ownerType.Name,
                comparisonType: StringComparison.Ordinal) &&
            actualType.Name.Length > ownerType.Name.Length &&
            actualType.Name[index: ownerType.Name.Length] == '[')
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a type obeys the required protocol when defining operator methods.
    /// For example, defining $add requires the type to obey Addable.
    /// </summary>
    private void ValidateOperatorProtocolConformance(RoutineInfo routineInfo,
        SourceLocation? location)
    {
        // Only check methods (not functions)
        if (routineInfo.OwnerType == null)
        {
            return;
        }

        // Get the required protocol for this wired method
        IReadOnlyList<string>? requiredProtocols = GetRequiredProtocols(wiredName: routineInfo.Name);
        if (requiredProtocols == null || requiredProtocols.Count == 0)
        {
            return; // Not an operator method or no protocol required
        }

        // Re-lookup the owner type to get the updated version with protocols
        TypeSymbol? currentOwnerType = _registry.LookupType(name: routineInfo.OwnerType.FullName);
        if (currentOwnerType == null)
        {
            return;
        }

        // Check if the owner type EXPLICITLY obeys the required protocol
        // (structural conformance doesn't count - you must declare "obeys Protocol")
        bool followsAny = requiredProtocols.Any(predicate: proto =>
            ExplicitlyFollowsProtocol(type: currentOwnerType, protocolName: proto));
        if (!followsAny)
        {
            string protocolText = requiredProtocols.Count == 1
                ? $"'{requiredProtocols[0]}'"
                : string.Join(separator: " or ", values: requiredProtocols.Select(selector: p => $"'{p}'"));
            ReportError(code: SemanticDiagnosticCode.OperatorWithoutProtocol,
                message:
                $"Type '{currentOwnerType.Name}' defines '{routineInfo.Name}' but does not follow {protocolText}. " +
                $"Add the matching 'obeys' protocol to the type declaration.",
                location: location);
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

        // Set _currentRoutine so IsGenericParameter() can find generic params like T, To, From
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = routineInfo;

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

        _currentRoutine = prevRoutine;

        // Update the routine info with resolved parameters and generic info
        _registry.UpdateRoutine(routine: routineInfo,
            parameters: parameters,
            returnType: returnType,
            genericParameters: externalDecl.GenericParameters,
            genericConstraints: externalDecl.GenericConstraints);
    }

    #endregion
}
