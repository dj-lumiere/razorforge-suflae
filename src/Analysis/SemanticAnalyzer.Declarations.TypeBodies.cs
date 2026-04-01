namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    #region Phase 2: Type Body Resolution

    /// <summary>
    /// Looks up a type by name, trying the current module-qualified name first,
    /// then falling back to the bare name. This is needed because Phase 1 registers
    /// user types with their full name (e.g., "Module.Point"), but Phase 2 resolvers
    /// only have the bare name from the AST node.
    /// </summary>
    private TypeInfo? LookupTypeInCurrentModule(string name)
    {
        string? moduleName = GetCurrentModuleName();
        if (moduleName != null)
        {
            TypeInfo? qualified = _registry.LookupType(name: $"{moduleName}.{name}");
            if (qualified != null)
            {
                return qualified;
            }
        }

        return _registry.LookupType(name: name);
    }

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
            ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty record body requires 'pass' keyword.",
                location: record.Location);
        }

        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeMemberVariableNames;

        _currentType = LookupTypeInCurrentModule(name: record.Name);
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
                    ReportError(code: SemanticDiagnosticCode.NotAProtocol,
                        message:
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                        location: protoExpr.Location);
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

                // Records can only contain value types + storable wrappers (Snatched, Retained, Shared, Tracked, Marked)
                // Scoped tokens (Viewed, Hijacked, Inspected, Seized), entities, and reference tuples are not allowed
                if (memberVariableType != null &&
                    memberVariableType is not ErrorTypeInfo &&
                    memberVariableType is not GenericParameterTypeInfo &&
                    !TypeRegistry.IsValueType(type: memberVariableType) &&
                    !(memberVariableType is WrapperTypeInfo wrapper &&
                      StorableWrapperTypes.Contains(item: GetBaseTypeName(typeName: wrapper.Name))))
                {
                    ReportError(code: SemanticDiagnosticCode.RecordContainsNonValueType,
                        message:
                        $"Record member variable '{memberVariable.Name}' has type '{memberVariableType.Name}' which is not a value type. " +
                        "Records can only contain value types, Snatched[T], and RC wrappers (Retained, Shared, Tracked, Marked).",
                        location: memberVariable.Location);
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
            ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty entity body requires 'pass' keyword.",
                location: entity.Location);
        }

        TypeSymbol? previousType = _currentType;
        HashSet<string>? previousFieldNames = _currentTypeMemberVariableNames;

        _currentType = LookupTypeInCurrentModule(name: entity.Name);
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
                    ReportError(code: SemanticDiagnosticCode.NotAProtocol,
                        message:
                        $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                        location: protoExpr.Location);
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

    private void ResolveProtocolBody(ProtocolDeclaration protocol)
    {
        // Look up the registered protocol type
        TypeSymbol? protoType = LookupTypeInCurrentModule(name: protocol.Name);
        if (protoType is not ProtocolTypeInfo protocolInfo)
        {
            return;
        }

        // Set current type context so generic parameters (T, U, etc.) are recognized
        TypeSymbol? previousType = _currentType;
        _currentType = protocolInfo;

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
                ReportError(code: SemanticDiagnosticCode.NotAProtocol,
                    message:
                    $"'{parentExpr}' is not a protocol. Only protocols can be inherited with 'obeys'.",
                    location: parentExpr.Location);
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

        _currentType = previousType;
    }

    private void ResolveVariantBody(VariantDeclaration variant)
    {
        if (variant.Members.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.EmptyEnumerationBody,
                message: $"Variant type '{variant.Name}' must have at least one member.",
                location: variant.Location);
            return;
        }

        // Resolve each member type and build VariantMemberInfo list
        var members = new List<VariantMemberInfo>();
        var seenTypeNames = new HashSet<string>();
        int nextTag = 0;
        bool hasNone = false;

        foreach (VariantMember member in variant.Members)
        {
            string typeName = member.Type.Name;

            // Handle None state (zero-sized, no payload)
            if (typeName == "None")
            {
                if (hasNone)
                {
                    ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                        message: $"Variant type '{variant.Name}' has duplicate 'None' member.",
                        location: member.Location);
                    continue;
                }

                hasNone = true;
                // None is always tag 0
                members.Insert(index: 0,
                    item: VariantMemberInfo.CreateNone(tagValue: 0, location: member.Location));
                continue;
            }

            TypeSymbol memberType = ResolveType(typeExpr: member.Type);

            // Check for duplicate types
            string memberTypeName = memberType.Name;
            if (!seenTypeNames.Add(item: memberTypeName))
            {
                ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                    message:
                    $"Variant type '{variant.Name}' has duplicate member type '{memberTypeName}'.",
                    location: member.Location);
                continue;
            }

            // Validate that tokens cannot be used as variant members
            ValidateNotTokenVariantPayload(type: memberType,
                caseName: memberTypeName,
                location: member.Location);

            // #59: Variant members cannot hold nested variants, Result[T], or Lookup[T]
            if (memberType is VariantTypeInfo)
            {
                ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                    message: $"Variant member '{memberTypeName}' cannot be a nested variant type.",
                    location: member.Location);
            }
            else if (memberType is ErrorHandlingTypeInfo
                     {
                         Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup
                     })
            {
                ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                    message: $"Variant member '{memberTypeName}' cannot be '{memberType.Name}'. " +
                             "Use failable routines (!) instead of storing Result/Lookup in variants.",
                    location: member.Location);
            }

            members.Add(item: new VariantMemberInfo(type: (TypeInfo)memberType)
            {
                Location = member.Location
            });
        }

        // Assign tag values: None = 0 (already set), others from 1
        int tagStart = hasNone
            ? 1
            : 0;
        foreach (VariantMemberInfo m in members)
        {
            if (!m.IsNone)
            {
                // Use reflection-free approach: find and update tag
                // Members are mutable via init, so we need to rebuild
            }
        }

        // Rebuild with correct tags
        var finalMembers = new List<VariantMemberInfo>();
        int tag = 0;
        // None first (tag 0) if present
        VariantMemberInfo? noneMember = members.FirstOrDefault(predicate: m => m.IsNone);
        if (noneMember != null)
        {
            finalMembers.Add(item: noneMember); // already tag 0
            tag = 1;
        }

        // Then all other members
        foreach (VariantMemberInfo m in members.Where(predicate: m => !m.IsNone))
        {
            finalMembers.Add(item: new VariantMemberInfo(type: m.Type!)
            {
                TagValue = tag++, Location = m.Location
            });
        }

        // Update the registered type with resolved members
        if (LookupTypeInCurrentModule(name: variant.Name) is VariantTypeInfo variantType)
        {
            var updated = new VariantTypeInfo(name: variant.Name)
            {
                Members = finalMembers,
                GenericParameters = variant.GenericParameters,
                GenericConstraints = variant.GenericConstraints,
                Visibility = variantType.Visibility,
                Location = variant.Location,
                Module = variantType.Module
            };
            _registry.UpdateType(oldType: variantType, newType: updated);
        }
    }

    /// <summary>
    /// Resolves choice body, populating the choice cases.
    /// </summary>
    private void ResolveChoiceBody(ChoiceDeclaration choice)
    {
        if (choice.Cases.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.EmptyEnumerationBody,
                message: $"Choice type '{choice.Name}' must have at least one case.",
                location: choice.Location);
            return;
        }

        TypeSymbol? choiceType = LookupTypeInCurrentModule(name: choice.Name);
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
                long? longValue = TryEvaluateChoiceCaseValue(expression: caseDecl.Value,
                    choice: choice,
                    caseName: caseDecl.Name,
                    location: caseDecl.Location);
                if (longValue.HasValue)
                {
                    if (longValue.Value < int.MinValue || longValue.Value > int.MaxValue)
                    {
                        ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
                            message:
                            $"Choice '{choice.Name}' case '{caseDecl.Name}': explicit value {longValue.Value} exceeds S32 range.",
                            location: caseDecl.Location);
                    }
                    else
                    {
                        explicitValue = (int)longValue.Value;
                    }
                }

                if (explicitValue.HasValue)
                {
                    autoValue = explicitValue.Value;
                    // Check auto-increment overflow
                    if (autoValue == int.MaxValue)
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

            int computedValue;
            if (explicitValue.HasValue)
            {
                computedValue = explicitValue.Value;
            }
            else
            {
                computedValue = autoValue;
                if (autoValue == int.MaxValue)
                {
                    ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
                        message:
                        $"Choice '{choice.Name}' case '{caseDecl.Name}': auto-assigned value would overflow S32 range.",
                        location: caseDecl.Location);
                }
                else
                {
                    autoValue += 1;
                }
            }

            cases.Add(item: new ChoiceCaseInfo(name: caseDecl.Name)
            {
                Value = explicitValue,
                ComputedValue = computedValue,
                Location = caseDecl.Location
            });
        }

        // Validate all-or-nothing explicit values
        int explicitCount = choice.Cases.Count(predicate: c => c.Value != null);
        if (explicitCount > 0 && explicitCount < choice.Cases.Count)
        {
            ReportError(code: SemanticDiagnosticCode.ChoiceMixedValues,
                message: $"Choice '{choice.Name}' mixes explicit and implicit case values. " +
                         "Either all cases must have explicit values or none should.",
                location: choice.Location);
        }

        // Validate no duplicate computed values
        var seenValues = new Dictionary<long, string>();
        foreach (ChoiceCaseInfo caseInfo in cases)
        {
            if (seenValues.TryGetValue(key: caseInfo.ComputedValue,
                    value: out string? existingCase))
            {
                ReportError(code: SemanticDiagnosticCode.ChoiceDuplicateValue,
                    message:
                    $"Choice '{choice.Name}' case '{caseInfo.Name}' has the same value ({caseInfo.ComputedValue}) as case '{existingCase}'.",
                    location: caseInfo.Location ?? choice.Location);
            }
            else
            {
                seenValues[key: caseInfo.ComputedValue] = caseInfo.Name;
            }
        }

        // Update the choice with resolved cases
        _registry.UpdateChoiceCases(choiceName: choiceInfo.FullName, cases: cases);
    }

    private void ResolveFlagsBody(FlagsDeclaration flags)
    {
        if (flags.Members.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.EmptyEnumerationBody,
                message: $"Flags type '{flags.Name}' must have at least one member.",
                location: flags.Location);
            return;
        }

        TypeSymbol? flagsType = LookupTypeInCurrentModule(name: flags.Name);
        if (flagsType is not FlagsTypeInfo flagsInfo)
        {
            return;
        }

        // #127: Max 64 members (U64 backing)
        if (flags.Members.Count > 64)
        {
            ReportError(code: SemanticDiagnosticCode.FlagsTooManyMembers,
                message:
                $"Flags type '{flags.Name}' has {flags.Members.Count} members, but the maximum is 64.",
                location: flags.Location);
        }

        var members = new List<FlagsMemberInfo>();
        var seenNames = new HashSet<string>();

        for (int i = 0; i < flags.Members.Count; i++)
        {
            string memberName = flags.Members[index: i];

            // Validate no duplicate member names
            if (!seenNames.Add(item: memberName))
            {
                ReportError(code: SemanticDiagnosticCode.FlagsDuplicateMember,
                    message: $"Flags type '{flags.Name}' has duplicate member '{memberName}'.",
                    location: flags.Location);
                continue;
            }

            members.Add(item: new FlagsMemberInfo(Name: memberName, BitPosition: i));
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

        ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
            message:
            $"Choice '{choice.Name}' case '{caseName}': value must be an integer literal.",
            location: location);
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
            string cleaned = CleanNumericLiteral(value: strVal);
            if (TryParseSignedInteger(value: cleaned, result: out long result))
            {
                return result;
            }
        }

        return ReportChoiceValueError(choice: choice, caseName: caseName, location: location);
    }

    private long? ReportChoiceValueError(ChoiceDeclaration choice, string caseName,
        SourceLocation location)
    {
        ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
            message:
            $"Choice '{choice.Name}' case '{caseName}': value must be an integer literal within S64 range.",
            location: location);
        return null;
    }

    #endregion
}
