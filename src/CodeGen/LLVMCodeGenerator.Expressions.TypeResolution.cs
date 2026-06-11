using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation helpers for result type resolution and conditional lowering.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Resolves the identifier type from semantic compiler state.
    /// </summary>
    private TypeInfo? ResolveIdentifierType(IdentifierExpression id)
    {
        if (_localVariables.TryGetValue(key: id.Name, value: out TypeInfo? varType))
        {
            return ApplyTypeSubstitutions(type: varType);
        }

        VariableInfo? regVar = _registry.LookupVariable(name: id.Name);
        return regVar != null
            ? ApplyTypeSubstitutions(type: regVar.Type)
            : null;
    }

    /// <summary>
    /// Gets the type of an expression (from semantic analysis metadata).
    /// </summary>
    private TypeInfo? GetExpressionType(Expression expr)
    {
        // For identifier expressions (and named-argument wrappers around them), prefer the
        // concrete local-variable type when it is more specific than a stale semantic annotation.
        // This fixes two cases:
        // 1. Monomorphization: ResolvedType may still carry unsubstituted generic params.
        // 2. Synthesized variant bodies: copied AST nodes can retain an overload-context type
        // that disagrees with the routine's actual parameter table (e.g., "from" marked S8
        // inside try_create(from: S32)).
            string? innerIdName = expr switch
            {
                IdentifierExpression idE => idE.Name,
                NamedArgumentExpression { Value: IdentifierExpression namedId } => namedId.Name,
                _ => null
            };
            if (innerIdName != null &&
                _localVariables.TryGetValue(key: innerIdName, value: out TypeInfo? localVarType))
            {
                TypeInfo concreteLocal = ApplyTypeSubstitutions(type: localVarType);
                if (concreteLocal is not GenericParameterTypeInfo && !concreteLocal.IsGenericDefinition && (expr.ResolvedType is null or ErrorTypeInfo or GenericParameterTypeInfo ||
                        ShouldPreferLocalIdentifierType(localType: concreteLocal,
                            resolvedType: expr.ResolvedType)))
                {
                    return concreteLocal;
                }
            }
        // First, check if the semantic analyzer has already resolved the type
        if (expr.ResolvedType is null or ErrorTypeInfo)
        {
            return expr switch
            {
                LiteralExpression literal => GetLiteralType(literal: literal),
                IdentifierExpression id => ResolveIdentifierType(id: id),
                MemberExpression member => GetMemberType(member: member),
                CreatorExpression ctor => ResolveCreatorType(creator: ctor),
                BinaryExpression binary => GetBinaryExpressionType(binary: binary),
                ChainedComparisonExpression => _registry.LookupType(
                    name: "Bool"), // Comparisons return Bool
                UnaryExpression unary => GetUnaryExpressionType(unary: unary),
                CallExpression call => GetCallReturnType(call: call),
                GenericMethodCallExpression gmc2 => throw new InvalidOperationException(
                    $"GenericMethodCallExpression must be lowered by GenericCallLoweringPass before codegen. " +
                    $"GMCE: {(gmc2.Object is IdentifierExpression eid ? eid.Name : gmc2.Object.GetType().Name)}.{gmc2.MethodName}" +
                    $"[{string.Join(", ", gmc2.TypeArguments?.Select(t => t.Name) ?? [])}], " +
                    $"in routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
                StealExpression steal => GetExpressionType(expr: steal.Operand),
                IndexExpression index => GetIndexReturnType(index: index),
                NamedArgumentExpression named => GetExpressionType(expr: named.Value),
                DictEntryLiteralExpression dictEntry => dictEntry.ResolvedType,
                ConditionalExpression cond => GetExpressionType(expr: cond.TrueExpression),
                GenericMemberExpression gme => GetGenericMemberExpressionType(gme: gme),
                _ => null
            };
        }

        // Skip SA-resolved type for CallExpression through transparent protocols (e.g., Referring[T]).
        // The SA may resolve "other[j]" on a Referring[Text] parameter to "Text" (the inner type),
        // but the correct return type is "Character" (from Text.$getitem!). GetCallReturnType
        // handles this via the transparent-protocol fallback path.
        bool skipSaResolved = false;
        if (expr is CallExpression { Callee: MemberExpression calleeMember })
        {
            TypeInfo? rcvrType = GetExpressionType(expr: calleeMember.Object);
            if (rcvrType is ProtocolTypeInfo { Methods.Count: 0, TypeArguments.Count: > 0 })
            {
                skipSaResolved = true;
            }
        }

        if (!skipSaResolved)
        {
            // During monomorphization, resolve unsubstituted generic params (e.g., Hijacked[U] -> Hijacked[S64])
            TypeInfo resolved = ApplyTypeSubstitutions(type: expr.ResolvedType);
            // If the type is still an unresolved generic parameter or an error placeholder,
            // fall through to the expression-specific resolution which can use call-site type arguments
            if (resolved is not GenericParameterTypeInfo and not ErrorTypeInfo)
            {
                // Const generic values resolve to their underlying primitive type for method dispatch
                if (resolved is ConstGenericValueTypeInfo constVal)
                {
                    return ResolveConstGenericUnderlyingType(constVal: constVal);
                }

                return resolved;
            }
        }

        // Fall back to inferring from the expression structure
        return expr switch
        {
            LiteralExpression literal => GetLiteralType(literal: literal),
            IdentifierExpression id => ResolveIdentifierType(id: id),
            MemberExpression member => GetMemberType(member: member),
            CreatorExpression ctor => ResolveCreatorType(creator: ctor),
            BinaryExpression binary => GetBinaryExpressionType(binary: binary),
            ChainedComparisonExpression => _registry.LookupType(
                name: "Bool"), // Comparisons return Bool
            UnaryExpression unary => GetUnaryExpressionType(unary: unary),
            CallExpression call => GetCallReturnType(call: call),
            GenericMethodCallExpression gmc2 => throw new InvalidOperationException(
                $"GenericMethodCallExpression must be lowered by GenericCallLoweringPass before codegen. " +
                $"GMCE: {(gmc2.Object is IdentifierExpression eid ? eid.Name : gmc2.Object.GetType().Name)}.{gmc2.MethodName}" +
                $"[{string.Join(", ", gmc2.TypeArguments?.Select(t => t.Name) ?? [])}], " +
                $"in routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            StealExpression steal => GetExpressionType(expr: steal.Operand),
            IndexExpression index => GetIndexReturnType(index: index),
            NamedArgumentExpression named => GetExpressionType(expr: named.Value),
            DictEntryLiteralExpression dictEntry => dictEntry.ResolvedType,
            ConditionalExpression cond => GetExpressionType(expr: cond.TrueExpression),
            GenericMemberExpression gme => GetGenericMemberExpressionType(gme: gme),
            _ => null
        };
    }

    /// <summary>
    /// Returns whether should prefer local identifier type applies in the current compiler context.
    /// </summary>
    private static bool ShouldPreferLocalIdentifierType(TypeInfo localType, TypeInfo resolvedType)
    {
        if (localType.FullName == resolvedType.FullName)
        {
            return false;
        }

        return IsFixedWidthScalarName(name: localType.Name) &&
               IsFixedWidthScalarName(name: resolvedType.Name);
    }

    /// <summary>
    /// Returns whether is fixed width scalar name applies in the current compiler context.
    /// </summary>
    private static bool IsFixedWidthScalarName(string name)
    {
        if (string.IsNullOrEmpty(value: name) || name.Length < 2)
        {
            return false;
        }

        return (name[0] == 'S' || name[0] == 'U') &&
               int.TryParse(s: name[1..], result: out _);
    }

    /// <summary>
    /// Resolves the creator type from semantic compiler state.
    /// </summary>
    private TypeInfo? ResolveCreatorType(CreatorExpression creator)
    {
        if (creator.ConstructedType is not null and not ErrorTypeInfo)
        {
            return ApplyTypeSubstitutions(type: creator.ConstructedType);
        }

        if (creator.ResolvedType is not null and not ErrorTypeInfo)
        {
            return ApplyTypeSubstitutions(type: creator.ResolvedType);
        }

        TypeInfo? tupleType = ResolveTupleTypeExpression(typeExpr: new TypeExpression(
            Name: creator.TypeName,
            GenericArguments: creator.TypeArguments,
            Location: creator.Location));
        if (tupleType != null)
        {
            return tupleType;
        }

        TypeInfo? type = LookupTypeInCurrentModule(name: creator.TypeName);
        if (type == null)
        {
            return null;
        }

        if (type.IsGenericDefinition && creator.TypeArguments is { Count: > 0 })
        {
            var resolvedArgs = new List<TypeInfo>(capacity: creator.TypeArguments.Count);
            foreach (TypeExpression ta in creator.TypeArguments)
            {
                TypeInfo? resolved = ResolveTypeArgument(ta: ta);
                if (resolved == null)
                {
                    return type;
                }

                resolvedArgs.Add(item: resolved);
            }

            if (resolvedArgs.Count == type.GenericParameters?.Count)
            {
                return _registry.GetOrCreateResolution(genericDef: type,
                    typeArguments: resolvedArgs);
            }
        }

        return type;
    }

    /// <summary>
    /// Gets the return type of an index expression by looking up $getitem on the target type.
    /// </summary>
    private TypeInfo? GetUnaryExpressionType(UnaryExpression unary)
    {
        TypeInfo? operandType = GetExpressionType(expr: unary.Operand);
        if (unary.Operator == UnaryOperator.ForceUnwrap && operandType != null && IsCarrierType(type: operandType) && operandType.TypeArguments is { Count: 1 })
        {
            // Force-unwrap: return the value type inside the Maybe/Result/Lookup wrapper
            return operandType.TypeArguments[index: 0];
        }

        return operandType;
    }

    /// <summary>
    /// Gets the binary expression type needed by this compiler phase.
    /// </summary>
    private TypeInfo? GetBinaryExpressionType(BinaryExpression binary)
    {
        return binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterEqual or BinaryOperator.And or BinaryOperator.Or or BinaryOperator.In or BinaryOperator.NotIn
            ? _registry.LookupType(name: "Bool")
            : GetExpressionType(expr: binary.Left);
    }

    /// <summary>
    /// Gets the type of a GenericMemberExpression (member access + indexing).
    /// </summary>
    private TypeInfo? GetGenericMemberExpressionType(GenericMemberExpression gme)
    {
        // Get the type of the object
        TypeInfo? objType = GetExpressionType(expr: gme.Object);
        switch (objType)
        {
            case null:
                return null;
            // Refresh stale generic entity resolutions (same as GetMemberType).
            // EntityTypeInfo.CreateInstance uses cycle detection that returns a shell with empty
            // MemberVariables when recursion is detected. The shell has GenericDefinition set,
            // so we can refresh it from the definition with the same type arguments.
            case EntityTypeInfo
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef,
                TypeArguments: not null
            } staleEntity:
            {
                var refreshed = genDef.CreateInstance(typeArguments: staleEntity.TypeArguments!) as EntityTypeInfo;
                if (refreshed is { MemberVariables.Count: > 0 })
                {
                    objType = refreshed;
                }

                break;
            }
        }

        // Find the member variable
        List<MemberVariableInfo>? memberVars = objType switch
        {
            EntityTypeInfo e => e.MemberVariables,
            RecordTypeInfo r => r.MemberVariables,
            _ => null
        };
        MemberVariableInfo? memberVar =
            memberVars?.FirstOrDefault(predicate: mv => mv.Name == gme.MemberName);
        if (memberVar?.Type == null)
        {
            return null;
        }

        // The member's type has type arguments -> the first one is the element type
        TypeInfo memberType = memberVar.Type;
        if (memberType.TypeArguments is { Count: > 0 })
        {
            return memberType.TypeArguments[index: 0];
        }

        // Try $getitem on the member type
        RoutineInfo? getItem = _registry.LookupMethod(type: memberType, methodName: "$getitem");
        return getItem?.ReturnType;
    }

    /// <summary>
    /// Gets the type of a literal expression from its token type.
    /// </summary>
    private TypeInfo? GetLiteralType(LiteralExpression literal)
    {
        string? typeName = literal.LiteralType switch
        {
            // Bare unsuffixed literals default to S64/F64 in RazorForge (same as SA rule).
            // Stdlib bodies bypass SA so we must handle these token types here.
            TokenType.IntegerLiteral => "S64",
            TokenType.DecimalLiteral => "F64",
            TokenType.S8Literal => "S8",
            TokenType.S16Literal => "S16",
            TokenType.S32Literal => "S32",
            TokenType.S64Literal => "S64",
            TokenType.S128Literal => "S128",
            TokenType.U8Literal => "U8",
            TokenType.U16Literal => "U16",
            TokenType.U32Literal => "U32",
            TokenType.U64Literal => "U64",
            TokenType.U128Literal => "U128",
            TokenType.F16Literal => "F16",
            TokenType.F32Literal => "F32",
            TokenType.F64Literal => "F64",
            TokenType.F128Literal => "F128",
            TokenType.D32Literal => "D32",
            TokenType.D64Literal => "D64",
            TokenType.D128Literal => "D128",
            TokenType.AddressLiteral => "Address",
            TokenType.True or TokenType.False => "Bool",
            TokenType.TextLiteral => "Text",
            TokenType.CharacterLiteral => "Character",
            TokenType.ByteLetterLiteral => "Byte",
            _ => null
        };

        return typeName != null
            ? _registry.LookupType(name: typeName)
            : null;
    }

    /// <summary>
    /// Gets the type of a member access expression.
    /// </summary>
    private TypeInfo? GetMemberType(MemberExpression member)
    {
        TypeInfo? targetType = GetExpressionType(expr: member.Object);
        if (targetType == null)
        {
            return null;
        }

        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        if (lookupType == null)
        {
            return null;
        }

        // Refresh stale entity metadata for member variable lookup.
        if (lookupType is EntityTypeInfo entityType)
        {
            lookupType = RefreshEntityMemberVariables(entity: entityType,
                memberVariableName: member.PropertyName);
        }

        MemberVariableInfo? memberVariable = lookupType switch
        {
            EntityTypeInfo e => e.LookupMemberVariable(memberVariableName: member.PropertyName),
            RecordTypeInfo r => r.LookupMemberVariable(memberVariableName: member.PropertyName),
            CrashableTypeInfo c => c.LookupMemberVariable(memberVariableName: member.PropertyName),
            _ => null
        };

        TypeInfo? memberType = memberVariable?.Type;
        if (memberType != null && lookupType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            memberType = ResolveGenericMemberType(memberType: memberType, ownerType: lookupType);
        }

        return memberType;
    }

    /// <summary>
    /// Gets the type bit width needed by this compiler phase.
    /// </summary>
    private int GetTypeBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            "ptr" => _pointerBitWidth,
            _ => throw new InvalidOperationException(
                message: $"Unknown LLVM type for bitwidth: {llvmType}")
        };
    }

    /// <summary>
    /// Performs the apply type substitutions step for this compiler phase.
    /// </summary>
    internal TypeInfo ApplyTypeSubstitutions(TypeInfo type)
    {
        if (type is WrapperTypeInfo wrapper)
        {
            TypeInfo? wrapperRecordDef = _registry.LookupType(name: wrapper.Name);
            if (wrapperRecordDef is { IsGenericDefinition: true } &&
                wrapper.TypeArguments is { Count: > 0 })
            {
                var resolvedArgs = _typeSubstitutions != null
                    ? wrapper.TypeArguments
                               .Select(selector: a => SubstituteTypeParams(type: a,
                                   substitutions: _typeSubstitutions))
                               .ToList()
                    : [.. wrapper.TypeArguments];
                return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                    typeArguments: resolvedArgs);
            }
        }

        if (_typeSubstitutions == null) return type;

        return SubstituteTypeParams(type: type, substitutions: _typeSubstitutions);
    }

    /// <summary>
    /// Performs the substitute type params step for this compiler phase.
    /// </summary>
    internal TypeInfo SubstituteTypeParams(TypeInfo type, Dictionary<string, TypeInfo> substitutions)
    {
        if (substitutions.TryGetValue(key: type.Name, value: out TypeInfo? sub))
            return sub;

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool needsResolution = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (TypeInfo ta in type.TypeArguments)
            {
                if (substitutions.TryGetValue(key: ta.Name, value: out TypeInfo? argSub))
                {
                    resolvedArgs.Add(item: argSub);
                    needsResolution = true;
                }
                else if (ta is { IsGenericResolution: true, TypeArguments: not null })
                {
                    TypeInfo innerResolved = SubstituteTypeParams(type: ta, substitutions: substitutions);
                    resolvedArgs.Add(item: innerResolved);
                    if (innerResolved != ta) needsResolution = true;
                }
                else if (ta is { IsGenericDefinition: true, GenericParameters: not null }
                         and not EntityTypeInfo)
                {
                    bool canResolve = true;
                    var innerArgs = new List<TypeInfo>();
                    foreach (string param in ta.GenericParameters)
                    {
                        if (substitutions.TryGetValue(key: param, value: out TypeInfo? paramSub))
                            innerArgs.Add(item: paramSub);
                        else { canResolve = false; break; }
                    }

                    if (canResolve)
                    {
                        resolvedArgs.Add(item: _registry.GetOrCreateResolution(genericDef: ta,
                            typeArguments: innerArgs));
                        needsResolution = true;
                    }
                    else resolvedArgs.Add(item: ta);
                }
                else resolvedArgs.Add(item: ta);
            }

            if (needsResolution)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: resolvedArgs);
            }
        }

        if (type is WrapperTypeInfo wrapperT)
        {
            TypeInfo resolvedInner = SubstituteTypeParams(type: wrapperT.InnerType,
                substitutions: substitutions);
            TypeInfo? wrapperRecordDef = _registry.LookupType(name: wrapperT.Name);
            if (wrapperRecordDef is { IsGenericDefinition: true })
                return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                    typeArguments: new List<TypeInfo> { resolvedInner });
            if (!ReferenceEquals(resolvedInner, wrapperT.InnerType))
                return new WrapperTypeInfo(wrapperName: wrapperT.Name, innerType: resolvedInner,
                    isReadOnly: wrapperT.IsReadOnly);
        }

        if (type is { IsGenericDefinition: true, GenericParameters: not null })
        {
            bool canResolve = true;
            var resolvedArgs = new List<TypeInfo>();
            foreach (string param in type.GenericParameters)
            {
                if (substitutions.TryGetValue(key: param, value: out TypeInfo? paramSub))
                    resolvedArgs.Add(item: paramSub);
                else { canResolve = false; break; }
            }

            if (canResolve && resolvedArgs.Count > 0)
                return _registry.GetOrCreateResolution(genericDef: type, typeArguments: resolvedArgs);
        }

        if (type is TupleTypeInfo tuple)
        {
            bool anyChanged = false;
            var resolvedElems = new List<TypeInfo>();
            foreach (TypeInfo elem in tuple.ElementTypes)
            {
                TypeInfo resolved = SubstituteTypeParams(type: elem, substitutions: substitutions);
                if (resolved != elem) anyChanged = true;
                resolvedElems.Add(item: resolved);
            }

            if (anyChanged) return new TupleTypeInfo(elementTypes: resolvedElems.ToList());
        }

        return type;
    }

    /// <summary>
    /// Resolves the type argument from semantic compiler state.
    /// </summary>
    private TypeInfo? ResolveTypeArgument(TypeExpression ta) // NOSONAR S3776
    {
        if (ta.ResolvedType is { } resolvedType and not ErrorTypeInfo)
        {
            return ApplyTypeSubstitutions(type: resolvedType);
        }

        if (TryParseConstGenericLiteral(name: ta.Name,
                value: out long constValue,
                explicitType: out string? explicitType))
        {
            return new ConstGenericValueTypeInfo(literalText: ta.Name,
                value: constValue,
                explicitTypeName: explicitType);
        }

        TypeInfo? tupleType = ResolveTupleTypeExpression(typeExpr: ta);
        if (tupleType != null)
        {
            return tupleType;
        }

        if (ta.GenericArguments is { Count: > 0 })
        {
            TypeInfo? baseType = _registry.LookupType(name: ta.Name);
            if (baseType != null)
            {
                var innerArgs = new List<TypeInfo>();
                foreach (TypeExpression innerTa in ta.GenericArguments)
                {
                    TypeInfo? innerResolved = ResolveTypeArgument(ta: innerTa);
                    if (innerResolved != null)
                    {
                        innerArgs.Add(item: innerResolved);
                    }
                }

                if (innerArgs.Count == (baseType.GenericParameters?.Count ?? 0))
                {
                    return _registry.GetOrCreateResolution(genericDef: baseType,
                        typeArguments: innerArgs);
                }
            }
        }

        TypeInfo? fromModule = LookupTypeInCurrentModule(name: ta.Name);
        if (fromModule != null)
        {
            return fromModule;
        }

        return _registry.LookupType(name: ta.Name);
    }

    /// <summary>
    /// Resolves the tuple type expression from semantic compiler state.
    /// </summary>
    private TupleTypeInfo? ResolveTupleTypeExpression(TypeExpression typeExpr)
    {
        if (typeExpr.Name is not "Tuple" and not "ValueTuple")
        {
            return null;
        }

        if (typeExpr.GenericArguments is not { Count: > 0 } elementTypeExprs)
        {
            return null;
        }

        var elementTypes = new List<TypeInfo>(capacity: elementTypeExprs.Count);
        foreach (TypeExpression elementTypeExpr in elementTypeExprs)
        {
            TypeInfo? elementType = ResolveTypeArgument(ta: elementTypeExpr);
            if (elementType == null)
            {
                return null;
            }

            elementTypes.Add(item: elementType);
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    /// <summary>
    /// Attempts to parse const generic literal and reports whether it succeeded.
    /// </summary>
    private static bool TryParseConstGenericLiteral(string name, out long value,
        out string? explicitType)
    {
        explicitType = null;

        if (long.TryParse(s: name, result: out value))
        {
            return true;
        }

        (string Suffix, string TypeName)[] integerSuffixes =
        [
            ("u8", "U8"), ("u16", "U16"), ("u32", "U32"), ("u64", "U64"), ("u128", "U128"),
            ("s8", "S8"), ("s16", "S16"), ("s32", "S32"), ("s64", "S64"), ("s128", "S128")
        ];

        foreach ((string suffix, string typeName) in integerSuffixes)
        {
            if (name.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s: name[..^suffix.Length], result: out value))
            {
                explicitType = typeName;
                return true;
            }
        }

        value = 0;
        return false;
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Attempts to get transparent protocol target and reports whether it succeeded.
    /// </summary>
    private static void TryGetTransparentProtocolTarget(TypeInfo? type, out TypeInfo? targetType)
    {
        if (type is ProtocolTypeInfo { TypeArguments: { Count: > 0 } } proto
            && HasOnlyMarkerCoercionMethods(proto))
        {
            targetType = proto.TypeArguments![index: 0]!;
            return;
        }

        targetType = type;
    }

    private static bool HasOnlyMarkerCoercionMethods(ProtocolTypeInfo proto)
    {
        foreach (ProtocolMethodInfo m in proto.Methods)
        {
            if (m.Name != "$refer" && m.Name != "$control") return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves a <see cref="ConstGenericValueTypeInfo"/> to its underlying primitive type
    /// for method dispatch. E.g., a const generic value "8" with constraint "N is U64"
    /// resolves to the U64 type so that method calls like N.$represent() work correctly.
    /// </summary>
    private TypeInfo ResolveConstGenericUnderlyingType(ConstGenericValueTypeInfo constVal)
    {
        string typeName = constVal.ExplicitTypeName ?? "U64";
        return _registry.LookupType(name: typeName) ?? constVal;
    }

    /// <summary>
    /// Gets the return type of an index expression by looking up $getitem on the target type.
    /// </summary>
    private TypeInfo? GetIndexReturnType(IndexExpression index) // NOSONAR S3776
    {
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        if (targetType == null)
        {
            return null;
        }

        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        if (lookupType == null)
        {
            return null;
        }

        RoutineInfo? getItem = _registry.LookupMethod(type: lookupType, methodName: "$getitem");
        if (getItem?.ReturnType == null)
        {
            return null;
        }

        TypeInfo returnType = getItem.ReturnType;
        List<string>? ownerGenericParams = null;
        if (lookupType.TypeArguments is { Count: > 0 })
        {
            TypeInfo? lookupGenericDef = lookupType switch
            {
                RecordTypeInfo { IsGenericResolution: true } r => r.GenericDefinition,
                EntityTypeInfo { IsGenericResolution: true } e => e.GenericDefinition,
                ProtocolTypeInfo { IsGenericResolution: true } p => p.GenericDefinition,
                _ => null
            };
            ownerGenericParams = lookupGenericDef?.GenericParameters ??
                                 getItem.OwnerType?.GenericParameters;
        }

        if (lookupType.TypeArguments is { Count: > 0 } && ownerGenericParams is { Count: > 0 })
        {
            for (int i = 0; i < ownerGenericParams.Count && i < lookupType.TypeArguments.Count; i++)
            {
                if (returnType.Name == ownerGenericParams[index: i])
                {
                    return lookupType.TypeArguments[index: i];
                }
            }

            var substitutions = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < ownerGenericParams.Count && i < lookupType.TypeArguments.Count; i++)
            {
                substitutions[key: ownerGenericParams[index: i]] =
                    lookupType.TypeArguments[index: i];
            }

            if (substitutions.Count > 0)
            {
                returnType = ApplyTypeSubstitutions(type: SubstituteTypeParams(type: returnType,
                    substitutions: substitutions));
            }
        }

        return returnType;
    }

    /// <summary>
    /// Gets the return type of a call expression.
    /// </summary>
    private TypeInfo? GetCallReturnType(CallExpression call) // NOSONAR S3776
    {
        // The emitted `call` targets ResolvedRoutine, and its LLVM return type is
        // GetLlvmType(ResolvedRoutine.ReturnType). So a FULLY CONCRETE resolved return type is
        // authoritative and must win over ConstructedType. This matters for a failable creator call
        // retargeted to its try_/check_/lookup_ variant (e.g. `S64(x)` → `S64.try_create`): the node
        // still records the bare constructed payload in ConstructedType (S64) while the routine
        // actually returns — and emits — the Maybe[S64] carrier. Sizing a spilled
        // `var __td_ret = S64(x)` off ConstructedType there yields `store i64 %maybeVal`, which fails
        // LLVM verification. When ReturnType is still generic (the universal `$create` returns `T`/
        // `Me`), it is not concrete, so we fall through to ConstructedType — preserving prior
        // behaviour for generic constructors.
        if (call.ResolvedRoutine?.ReturnType is { } resolvedReturn and not ErrorTypeInfo)
        {
            TypeInfo concreteReturn = ApplyTypeSubstitutions(type: resolvedReturn);
            if (concreteReturn is not GenericParameterTypeInfo and not ErrorTypeInfo
                && !concreteReturn.IsGenericDefinition
                && !ContainsGenericParameter(type: concreteReturn))
            {
                return concreteReturn;
            }
        }

        if (call.ConstructedType is not null and not ErrorTypeInfo)
        {
            TypeInfo constructed = ApplyTypeSubstitutions(type: call.ConstructedType);
            if (constructed is not GenericParameterTypeInfo and not ErrorTypeInfo)
            {
                return constructed;
            }
        }

        // Fallback: OperatorLoweringPass sets ResolvedType on $getitem! calls when it can't
        // find a RoutineInfo via LookupMethod (e.g., when registered name differs from lookup name).
        // ResolvedType was set from the IndexExpression SA annotated before lowering.
        if (call.ResolvedType is not null and not ErrorTypeInfo)
        {
            TypeInfo fallback = ApplyTypeSubstitutions(type: call.ResolvedType);
            if (fallback is not GenericParameterTypeInfo and not ErrorTypeInfo)
                return fallback;
        }

        // SA must set ResolvedRoutine or ConstructedType on every call before backend entry.
        // Report the ACTUAL annotation values: a call can carry a non-null ResolvedRoutine and
        // still land here when its return type never became concrete (e.g. a generic-def
        // resolution whose ReturnType still contains a type parameter).
        string calleeDesc = call.Callee switch
        {
            MemberExpression m => $"{m.Object.GetType().Name}.{m.PropertyName}",
            IdentifierExpression id => id.Name,
            _ => call.Callee.GetType().Name
        };
        string memberObjResolvedDesc = call.Callee is MemberExpression me
            ? me.Object.ResolvedType?.FullName ?? "<null>"
            : "<not member>";
        string resolvedRoutineDesc = call.ResolvedRoutine is { } rr
            ? $"{rr.FullName} -> {rr.ReturnType?.FullName ?? "<null>"}"
            : "<null>";
        throw new InvalidOperationException(
            $"CallExpression '{calleeDesc}' has no concrete SA-resolved return type " +
            $"(ResolvedRoutine={resolvedRoutineDesc}, " +
            $"ConstructedType={call.ConstructedType?.FullName ?? "<null>"}, " +
            $"ResolvedType={call.ResolvedType?.FullName ?? "<null>"}, " +
            $"ObjectResolvedType={memberObjResolvedDesc}). " +
            $"Semantic analysis must annotate all calls. Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"}.");
    }

}
