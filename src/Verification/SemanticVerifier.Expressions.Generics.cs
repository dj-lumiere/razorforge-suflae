using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private TypeSymbol AnalyzeGenericMethodCallExpression(GenericMethodCallExpression generic)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: generic.Object);

        // Resolve type arguments
        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            typeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // #19: Track lock policy from share[Policy]() on entities — stored temporarily
        // on the source variable; propagated to the declared variable in AnalyzeVariableDeclaration
        if (generic.MethodName == "share" && typeArgs.Count > 0 &&
            generic.Object is IdentifierExpression shareTarget)
        {
            _lastSharePolicy = (shareTarget.Name, typeArgs[index: 0].Name);
        }

        if (generic.IsCollectionLiteral)
            generic.LoweringKind = CallLoweringKind.CollectionConstruction;

        // Check if this is a generic type constructor call (e.g., Hijacked[U8](addr))
        // The parser creates GenericMethodCallExpression for both Type[Args](args) and obj.method[Args](args)
        if (generic.Object is IdentifierExpression typeId && objectType is TypeInfo
            {
                IsGenericDefinition: true
            } typeInfo && typeId.Name == generic.MethodName)
        {
            // Resolve the generic type with the provided type arguments
            TypeInfo resolvedType = _registry.GetOrCreateResolution(genericDef: typeInfo,
                typeArguments: typeArgs
                                       .ToList());
            generic.ConstructedType = resolvedType;
            generic.LoweringKind = ClassifyConstruction(type: resolvedType,
                isCollectionLiteral: generic.IsCollectionLiteral);

            var argTypes = new List<TypeSymbol>();
            foreach (Expression arg in generic.Arguments)
            {
                argTypes.Add(item: AnalyzeExpression(expression: arg));
            }

            {
                RoutineInfo? creator = _registry.LookupMethodOverload(type: resolvedType,
                    methodName: "$create",
                    argTypes: argTypes);
                creator ??= _registry.LookupRoutineOverload(
                    baseName: $"{resolvedType.Name}.$create",
                    argTypes: argTypes);

                if (creator != null && creator.Parameters.Count == argTypes.Count &&
                    !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
                {
                    generic.ResolvedRoutine = creator;
                    ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                        location: generic.Location);
                    // Prefer the concrete resolvedType (e.g. Hijacked[Byte]) over the creator's
                    // return type when that return type is still generic (e.g. Hijacked[T]).
                    // creator.ReturnType for Hijacked[T].$create is "Hijacked[T]" — a resolution
                    // whose TypeArguments contain GenericParameterTypeInfo placeholders.  Returning
                    // that causes downstream callers (.extract(), etc.) to see an unresolved type and
                    // mangle method names as "Core.Hijacked[T].extract" instead of the correct
                    // "Core.Hijacked[Core.Byte].extract".
                    bool returnTypeIsGenericOrUnresolved =
                        creator.ReturnType is null or { IsGenericDefinition: true } ||
                        creator.ReturnType.TypeArguments?.Any(
                            predicate: t => t is GenericParameterTypeInfo) == true;
                    return returnTypeIsGenericOrUnresolved ? resolvedType : creator.ReturnType!;
                }
            }

            int memberCount = resolvedType switch
            {
                EntityTypeInfo e => e.MemberVariables.Count,
                RecordTypeInfo r => r.MemberVariables.Count,
                _ => 0
            };
            if (memberCount >= 2)
            {
                foreach (Expression arg in generic.Arguments)
                {
                    if (arg is not NamedArgumentExpression)
                    {
                        ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                            message:
                            $"Type '{resolvedType.Name}' has {memberCount} fields - all constructor arguments must be named.",
                            location: arg.Location);
                    }
                }
            }

            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                location: generic.Location);
            return resolvedType;
        }

        // Look up the method on receiver type — LookupMethod handles generic resolutions
        RoutineInfo? method =
            _registry.LookupMethod(type: objectType, methodName: generic.MethodName);
        if (method != null)
        {
            generic.LoweringKind = ClassifyMethodCall(method: method);

            foreach (Expression arg in generic.Arguments)
            {
                AnalyzeExpression(expression: arg);
            }

            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                location: generic.Location);

            if (method.IsGenericDefinition)
            {
                // Owner-level generic params (e.g. T from Hijacked[T]) are bound by the receiver.
                // Compare typeArgs only against method-level params (e.g. U from recast_as[U]).
                var ownerGenericParamNames = GetOwnerGenericParameterNames(ownerType: objectType);
                List<string> methodOnlyParams =
                    method.GenericParameters?
                          .Where(predicate: gp => !ownerGenericParamNames.Contains(item: gp))
                          .ToList() ?? new List<string>();

                if (methodOnlyParams.Count != typeArgs.Count)
                {
                    ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                        message:
                        $"Method '{method.Name}' expects {methodOnlyParams.Count} type arguments, got {typeArgs.Count}.",
                        location: generic.Location);
                    return ErrorTypeInfo.Instance;
                }

                // Build full type-argument list aligned to method.GenericParameters order.
                // For owner-level params, take the binding from the receiver's TypeArguments;
                // for method-level params, take from the user-supplied typeArgs in order.
                List<TypeSymbol> fullTypeArgs;
                if (method.GenericParameters != null &&
                    method.GenericParameters.Count != typeArgs.Count)
                {
                    fullTypeArgs = new List<TypeSymbol>(capacity: method.GenericParameters.Count);
                    int methodArgIdx = 0;
                    var ownerBindings = BuildOwnerBindingMap(ownerType: objectType);
                    foreach (string paramName in method.GenericParameters)
                    {
                        if (ownerGenericParamNames.Contains(item: paramName) &&
                            ownerBindings.TryGetValue(key: paramName, value: out TypeInfo? ownerArg))
                        {
                            fullTypeArgs.Add(item: ownerArg);
                        }
                        else if (methodArgIdx < typeArgs.Count)
                        {
                            fullTypeArgs.Add(item: typeArgs[index: methodArgIdx++]);
                        }
                    }
                }
                else
                {
                    fullTypeArgs = typeArgs.ToList();
                }

                method = _registry.GetOrCreateRoutineResolution(genericDef: method,
                    typeArguments: fullTypeArgs);
            }

            generic.ResolvedRoutine = method;
            generic.LoweringKind = ClassifyMethodCall(method: method);
            generic.IsInFlight = method.IsInFlightReturn;

            if (method.ReturnType == null)
            {
                return _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
            }

            TypeSymbol returnType = method.ReturnType;

            // Substitute method's own generic params (U from obtain_as[U])
            // GenericParameters now contains only method-level params (owner-level params
            // are stripped by SubstituteMethodForOwner), so indices map directly to typeArgs.
            if (method.GenericParameters != null)
            {
                // Direct param (return type is just U)
                if (returnType is GenericParameterTypeInfo)
                {
                    int paramIndex = method.GenericParameters
                                           .ToList()
                                           .IndexOf(item: returnType.Name);
                    if (paramIndex >= 0 && paramIndex < typeArgs.Count &&
                        typeArgs[index: paramIndex] is TypeInfo resolved)
                    {
                        return resolved;
                    }
                }

                // Resolution containing method's params (e.g., Hijacked[U])
                if (returnType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    var substitutedArgs = new List<TypeInfo>();
                    bool anySubstituted = false;
                    foreach (TypeInfo typeArg in returnType.TypeArguments)
                    {
                        int idx = method.GenericParameters
                                        .ToList()
                                        .IndexOf(item: typeArg.Name);
                        if (idx >= 0 && idx < typeArgs.Count &&
                            typeArgs[index: idx] is TypeInfo sub)
                        {
                            substitutedArgs.Add(item: sub);
                            anySubstituted = true;
                        }
                        else
                        {
                            substitutedArgs.Add(item: typeArg);
                        }
                    }

                    if (anySubstituted)
                    {
                        TypeInfo? genericDef = GetGenericDefinition(resolution: returnType);
                        if (genericDef != null)
                        {
                            return _registry.GetOrCreateResolution(genericDef: genericDef,
                                typeArguments: substitutedArgs);
                        }
                    }
                }
            }

            return returnType;
        }

        // Standalone generic function call (e.g., ptrtoint[Point, Address](p), hijacked_none[T]())
        // The object is an identifier that resolves to a routine, not a type or variable
        if (generic.Object is IdentifierExpression funcId)
        {
            RoutineInfo? routine = _registry.LookupRoutine(fullName: funcId.Name) ??
                                   _registry.LookupRoutineByName(name: funcId.Name);
            if (routine != null)
            {
                if (routine.IsGenericDefinition)
                {
                    if (routine.GenericParameters == null ||
                        routine.GenericParameters.Count != typeArgs.Count)
                    {
                        ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                            message:
                            $"Routine '{routine.Name}' expects {routine.GenericParameters?.Count ?? 0} type arguments, got {typeArgs.Count}.",
                            location: generic.Location);
                        return ErrorTypeInfo.Instance;
                    }

                    routine = _registry.GetOrCreateRoutineResolution(genericDef: routine,
                        typeArguments: typeArgs.ToList());
                }

                generic.ResolvedRoutine = routine;
                generic.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);
                generic.IsInFlight = routine.IsInFlightReturn;

                foreach (Expression arg in generic.Arguments)
                {
                    AnalyzeExpression(expression: arg);
                }

                if (routine.ReturnType == null)
                {
                    return _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
                }

                // Substitute generic type parameters in return type
                TypeInfo returnType = routine.ReturnType;
                if (returnType is GenericParameterTypeInfo && routine.GenericParameters != null)
                {
                    int paramIndex = routine.GenericParameters
                                            .ToList()
                                            .IndexOf(item: returnType.Name);
                    if (paramIndex >= 0 && paramIndex < typeArgs.Count &&
                        typeArgs[index: paramIndex] is TypeInfo resolved)
                    {
                        return resolved;
                    }
                }

                // Return type is a generic resolution (e.g., Hijacked[T] -> Hijacked[U8])
                if (returnType.IsGenericResolution && returnType.TypeArguments != null &&
                    routine.GenericParameters != null)
                {
                    var substitutedArgs = new List<TypeInfo>();
                    bool anySubstituted = false;
                    foreach (TypeInfo typeArg in returnType.TypeArguments)
                    {
                        int idx = routine.GenericParameters
                                         .ToList()
                                         .IndexOf(item: typeArg.Name);
                        if (idx >= 0 && idx < typeArgs.Count &&
                            typeArgs[index: idx] is TypeInfo sub)
                        {
                            substitutedArgs.Add(item: sub);
                            anySubstituted = true;
                        }
                        else
                        {
                            substitutedArgs.Add(item: typeArg);
                        }
                    }

                    if (anySubstituted)
                    {
                        TypeInfo? genericDef = GetGenericDefinition(resolution: returnType);
                        if (genericDef != null)
                        {
                            return _registry.GetOrCreateResolution(genericDef: genericDef,
                                typeArguments: substitutedArgs);
                        }
                    }
                }

                return returnType;
            }
        }

        // Analyze arguments
        foreach (Expression arg in generic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeGenericMemberExpression(GenericMemberExpression genericMember) // NOSONAR S3776
    {
        TypeSymbol objectType = AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        var resolvedTypeArgs = new List<TypeInfo>(capacity: genericMember.TypeArguments.Count);
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            resolvedTypeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // Typewise receiver: `Ident[T]` parsed as GenericMemberExpression(Ident, Ident.Name, [T])
        // — the parser sets MemberName == Object.Name when the source was `Ident[Args]`, not
        // `obj.field[i]`. When that holds and Object resolves to a generic type, return the
        // monomorphized type so the outer `.method()` call has a proper typewise receiver type.
        if (genericMember.Object is IdentifierExpression idReceiver &&
            idReceiver.Name == genericMember.MemberName &&
            objectType.IsGenericDefinition &&
            resolvedTypeArgs.Count == objectType.GenericParameters?.Count)
        {
            TypeSymbol resolved = _registry.GetOrCreateResolution(
                genericDef: objectType,
                typeArguments: resolvedTypeArgs);
            genericMember.ResolvedType = resolved;
            return resolved;
        }

        // Look up the member on the object type
        List<MemberVariableInfo>? memberVars = objectType switch
        {
            EntityTypeInfo e => e.MemberVariables,
            RecordTypeInfo r => r.MemberVariables,
            _ => null
        };
        MemberVariableInfo? memberVar =
            memberVars?.FirstOrDefault(predicate: mv => mv.Name == genericMember.MemberName);
        if (memberVar != null)
        {
            // Member found — the [args] are indexing into the member's value.
            // Analyze the "type arguments" as expressions (they're actually index values).
            foreach (TypeExpression typeArg in genericMember.TypeArguments)
            {
                // The type arg's Name is actually a variable name — analyze it as identifier
                if (typeArg.Name != null)
                {
                    AnalyzeExpression(expression: new IdentifierExpression(Name: typeArg.Name,
                        Location: typeArg.Location));
                }
            }

            // Determine the element type of the member's collection type
            TypeInfo? memberType = memberVar.Type;
            if (memberType is { TypeArguments: { Count: > 0 } })
            {
                // e.g., List[SortedDict[K,V]]  element is SortedDict[K,V]
                return memberType.TypeArguments[index: 0];
            }

            // If the member type has a $getitem method, use its return type
            RoutineInfo? getItem =
                _registry.LookupMethod(type: memberType, methodName: "$getitem");
            if (getItem?.ReturnType != null)
            {
                return getItem.ReturnType;
            }

            return memberType;
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIsPatternExpression(IsPatternExpression isPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: isPat.Expression);

        // Analyze the pattern (may bind variables)
        AnalyzePattern(pattern: isPat.Pattern, matchedType: exprType);

        // 'is' expressions always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeFlagsTestExpression(FlagsTestExpression flagsTest) // NOSONAR S3776
    {
        TypeSymbol subjectType = AnalyzeExpression(expression: flagsTest.Subject);

        if (subjectType.Category == TypeCategory.Error)
        {
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        if (subjectType is not FlagsTypeInfo flagsType)
        {
            ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                message:
                $"Flags test operators (is/isnot/isonly) require a flags type, but got '{subjectType.Name}'.",
                location: flagsTest.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // #133: isonly rejects 'or' and 'but'
        if (flagsTest.Kind == FlagsTestKind.IsOnly)
        {
            if (flagsTest.Connective == FlagsTestConnective.Or)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut,
                    message:
                    "'isonly' cannot be used with 'or'. Use 'and' to specify the exact set of flags.",
                    location: flagsTest.Location);
            }

            if (flagsTest.ExcludedFlags is { Count: > 0 })
            {
                ReportError(code: SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut,
                    message:
                    "'isonly' cannot be used with 'but'. Specify the exact set of flags directly.",
                    location: flagsTest.Location);
            }
        }

        // Validate each flag name exists in the type
        foreach (string flagName in flagsTest.TestFlags)
        {
            if (flagsType.Members.All(predicate: m => m.Name != flagName))
            {
                ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                    message:
                    $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                    location: flagsTest.Location);
            }
        }

        // Validate excluded flags too
        if (flagsTest.ExcludedFlags != null)
        {
            foreach (string flagName in flagsTest.ExcludedFlags)
            {
                if (flagsType.Members.All(predicate: m => m.Name != flagName))
                {
                    ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                        message:
                        $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                        location: flagsTest.Location);
                }
            }
        }

        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private static Dictionary<string, TypeInfo> BuildOwnerBindingMap(TypeInfo? ownerType) // NOSONAR S3776
    {
        var map = new Dictionary<string, TypeInfo>();
        if (ownerType == null)
            return map;

        TypeInfo? def = ownerType switch
        {
            RecordTypeInfo r => r.GenericDefinition ?? (r.IsGenericDefinition ? r : null),
            EntityTypeInfo e => e.GenericDefinition ?? (e.IsGenericDefinition ? e : null),
            ProtocolTypeInfo p => p.GenericDefinition ?? (p.IsGenericDefinition ? p : null),
            WrapperTypeInfo w => w.IsGenericDefinition ? w : null,
            _ => ownerType.IsGenericDefinition ? ownerType : null
        };

        List<string>? paramNames =
            def?.GenericParameters ?? ownerType.GenericParameters;
        List<TypeInfo>? args = ownerType.TypeArguments;

        if (paramNames != null && args != null)
        {
            for (int i = 0; i < paramNames.Count && i < args.Count; i++)
            {
                map[key: paramNames[index: i]] = args[index: i];
            }
            return map;
        }

        // Fallback: receiver is an unsubstituted generic instance like Hijacked[T] inside its own
        // body. Map each param name to a same-named GenericParameterTypeInfo placeholder.
        if (paramNames != null)
        {
            foreach (string p in paramNames)
            {
                map[key: p] = new GenericParameterTypeInfo(name: p);
            }
        }
        return map;
    }

    private static HashSet<string> GetOwnerGenericParameterNames(TypeInfo? ownerType) // NOSONAR S3776
    {
        var names = new HashSet<string>();
        if (ownerType == null)
            return names;

        TypeInfo? def = ownerType switch
        {
            RecordTypeInfo r => r.GenericDefinition ?? (r.IsGenericDefinition ? r : null),
            EntityTypeInfo e => e.GenericDefinition ?? (e.IsGenericDefinition ? e : null),
            ProtocolTypeInfo p => p.GenericDefinition ?? (p.IsGenericDefinition ? p : null),
            WrapperTypeInfo w => w.IsGenericDefinition ? w : null,
            _ => ownerType.IsGenericDefinition ? ownerType : null
        };

        if (def?.GenericParameters != null)
        {
            foreach (string p in def.GenericParameters)
                names.Add(item: p);
        }
        else if (ownerType.GenericParameters != null)
        {
            foreach (string p in ownerType.GenericParameters)
                names.Add(item: p);
        }
        return names;
    }

    private ErrorTypeInfo HandleUnknownExpression(Expression expression)
    {
        ReportWarning(code: SemanticWarningCode.UnknownExpressionType,
            message: $"Unknown expression type: {expression.GetType().Name}",
            location: expression.Location);
        return ErrorTypeInfo.Instance;
    }
}
