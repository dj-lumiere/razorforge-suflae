namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
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

            case ExternalDeclaration externalDecl:
                ResolveExternalParameters(externalDecl: externalDecl);
                break;

            case ExternalBlockDeclaration block:
                foreach (Declaration decl in block.Declarations)
                {
                    ResolveRoutineSignature(node: decl);
                }

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
        // but the FullName includes the owner type, so we can look it up either way.
        // Module-qualified routines (e.g., "HelloWorld.divide") need module prefix for lookup.
        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: routineName);
        if (routineInfo == null && _currentModuleName != null)
        {
            routineInfo = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{routineName}");
        }

        // For generic type methods (e.g., "Box[T].convert"), strip the generic params from the
        // type name and retry (the routine was registered as "Box.convert")
        if (routineInfo == null && routineName.Contains(value: '['))
        {
            int bracketStart = routineName.IndexOf(value: '[');
            int bracketEnd = routineName.IndexOf(value: ']');
            if (bracketEnd > bracketStart)
            {
                string strippedName =
                    routineName[..bracketStart] + routineName[(bracketEnd + 1)..];
                routineInfo = _registry.LookupRoutine(fullName: strippedName);
            }
        }

        if (routineInfo == null)
        {
            return;
        }

        // Set _currentRoutine so IsGenericParameter() can find generic params like T, U
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = routineInfo;

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
            if (paramType is ErrorHandlingTypeInfo errorHandlingType &&
                errorHandlingType.Kind is ErrorHandlingKind.Result or ErrorHandlingKind.Lookup)
            {
                ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsParameter,
                    message:
                    $"'{errorHandlingType.Kind}[T]' cannot be used as a parameter type. " +
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
        if (returnType is ErrorHandlingTypeInfo errorHandlingReturn &&
            errorHandlingReturn.Kind is ErrorHandlingKind.Maybe or ErrorHandlingKind.Result
                or ErrorHandlingKind.Lookup && !IsStdlibFile(filePath: _currentFilePath))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType,
                message: $"Routine cannot return '{errorHandlingReturn.Kind}[T]'. " +
                         "These types are builder-generated for failable routines. " +
                         "Use a failable routine (!) with 'throw'/'absent' instead.",
                location: routine.ReturnType?.Location ?? routine.Location);
        }

        // Merge implicit generics with explicit generics
        List<string> allGenericParams = routineInfo.GenericParameters?.ToList() ?? [];
        allGenericParams.AddRange(collection: implicitGenerics);

        // Merge implicit constraints with explicit constraints
        List<GenericConstraintDeclaration> allConstraints =
            routineInfo.GenericConstraints?.ToList() ?? [];
        allConstraints.AddRange(collection: implicitConstraints);

        _currentRoutine = prevRoutine;

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

        // Validate operator protocol conformance for wired methods
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
            // For generic types, the actual type may be parameterized (e.g., "Total[T]")
            // while OwnerType.Name is the base name (e.g., "Total").
            if (expectedType is ProtocolSelfTypeInfo)
            {
                if (typeMethod.OwnerType != null &&
                    !MeTypeMatches(actualName: actualType.Name,
                        ownerName: typeMethod.OwnerType.Name))
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        message:
                        $"Parameter '{protoMethod.ParameterNames[index: i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location: location);
                }
            }
            else if (actualType.Name != expectedType.Name)
            {
                ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                    message:
                    $"Parameter '{protoMethod.ParameterNames[index: i]}' of '{typeMethod.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{expectedType.Name}'.",
                    location: location);
            }
        }

        // Check return type
        if (protoMethod.ReturnType != null && typeMethod.ReturnType != null)
        {
            TypeSymbol expectedReturn = protoMethod.ReturnType;
            TypeSymbol actualReturn = typeMethod.ReturnType;

            // Handle protocol self type (Me)
            // For generic types, the actual return may be parameterized (e.g., "Buffer[T]")
            // while OwnerType.Name is the base name (e.g., "Buffer").
            if (expectedReturn is ProtocolSelfTypeInfo)
            {
                if (typeMethod.OwnerType != null &&
                    !MeTypeMatches(actualName: actualReturn.Name,
                        ownerName: typeMethod.OwnerType.Name))
                {
                    ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                        message:
                        $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{typeMethod.OwnerType.Name}' (Me).",
                        location: location);
                }
            }
            else if (actualReturn.Name != expectedReturn.Name)
            {
                ReportError(code: SemanticDiagnosticCode.ProtocolMethodSignatureMismatch,
                    message:
                    $"Method '{typeMethod.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{expectedReturn.Name}'.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Checks if an actual type name matches the owner type name for protocol Me type validation.
    /// For generic types, "Total[T]" matches owner "Total" (parameterized form of the same type).
    /// </summary>
    private static bool MeTypeMatches(string actualName, string ownerName)
    {
        if (actualName == ownerName)
        {
            return true;
        }

        // Accept parameterized form: "Total[T]" matches "Total"
        return actualName.StartsWith(value: ownerName, comparisonType: StringComparison.Ordinal) &&
               actualName.Length > ownerName.Length &&
               actualName[ownerName.Length] == '[';
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
        string? requiredProtocol = GetRequiredProtocol(wiredName: routineInfo.Name);
        if (requiredProtocol == null)
        {
            return; // Not an operator method or no protocol required
        }

        // Re-lookup the owner type to get the updated version with protocols
        // (the RoutineInfo.OwnerType may reference an older object from Phase 1)
        TypeSymbol? currentOwnerType = _registry.LookupType(name: routineInfo.OwnerType.FullName);
        if (currentOwnerType == null)
        {
            return;
        }

        // Check if the owner type EXPLICITLY obeys the required protocol
        // (structural conformance doesn't count - you must declare "obeys Protocol")
        if (!ExplicitlyFollowsProtocol(type: currentOwnerType, protocolName: requiredProtocol))
        {
            ReportError(code: SemanticDiagnosticCode.OperatorWithoutProtocol,
                message:
                $"Type '{currentOwnerType.Name}' defines '{routineInfo.Name}' but does not follow '{requiredProtocol}'. " +
                $"Add 'obeys {requiredProtocol}' to the type declaration.",
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
