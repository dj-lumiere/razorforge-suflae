namespace Compiler.CodeGen;

using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation helpers for result type resolution and conditional lowering.
/// </summary>
public partial class LLVMCodeGenerator
{
    private TypeInfo? ResolveIdentifierType(IdentifierExpression id)
    {
        if (_localVariables.TryGetValue(key: id.Name, value: out TypeInfo? varType))
        {
            return ApplyTypeSubstitutions(type: varType);
        }

        VariableInfo? regVar = _registry.LookupVariable(name: id.Name);
        if (regVar != null)
        {
            return ApplyTypeSubstitutions(type: regVar.Type);
        }

        // Generic type param or const generic — resolve via substitutions
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: id.Name, value: out TypeInfo? sub))
        {
            if (sub is ConstGenericValueTypeInfo constVal)
            {
                return ResolveConstGenericUnderlyingType(constVal: constVal);
            }

            return sub;
        }

        return null;
    }

    /// <summary>
    /// Resolves a <see cref="ConstGenericValueTypeInfo"/> to its underlying primitive type
    /// for method dispatch. E.g., a const generic value "8" with constraint "N is U64"
    /// resolves to the U64 type so that method calls like N.$represent() work correctly.
    /// </summary>
    private TypeInfo ResolveConstGenericUnderlyingType(ConstGenericValueTypeInfo constVal)
    {
        // Use explicit type if available (e.g., "4u64" → U64)
        string
            typeName =
                constVal.ExplicitTypeName ??
                "U64"; // Default to U64 for untyped integer const generics
        return _registry.LookupType(name: typeName) ?? constVal;
    }

    /// <summary>
    /// Resolves a type name that appears as a method receiver during monomorphization.
    /// E.g., T.data_size() where T is still "T" — resolved via <see cref="_typeSubstitutions"/> key lookup.
    /// Falls back to registry lookup for non-monomorphization paths.
    /// </summary>
    private TypeInfo? ResolveTypeNameAsReceiver(string name)
    {
        // Generic type param used as receiver (e.g., T.data_size() where T is still "T")
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: name, value: out TypeInfo? sub) &&
            sub is not ConstGenericValueTypeInfo)
        {
            return sub;
        }

        // Direct registry lookup (handles simple names like S64, Text, Character)
        TypeInfo? type = LookupTypeInCurrentModule(name: name);
        if (type != null)
        {
            return type;
        }

        // During monomorphization, check if this name matches any substituted type
        // (handles generic instances like SortedDict[S64, S64] that may not be in the registry by bare name)
        if (_typeSubstitutions != null)
        {
            foreach (TypeInfo sub2 in _typeSubstitutions.Values)
            {
                if (sub2 is ConstGenericValueTypeInfo)
                {
                    continue;
                }

                if (sub2.Name == name)
                {
                    return sub2;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the type of an expression (from semantic analysis metadata).
    /// </summary>
    private TypeInfo? GetExpressionType(Expression expr)
    {
        // For index expressions during monomorphization, prefer structure-based inference.
        // ResolvedType may contain ambiguous generic params (e.g., T) that map to the wrong
        // substitution when the collection's element type differs from the outer type param.
        if (expr is IndexExpression indexExpr && _typeSubstitutions != null)
        {
            TypeInfo? structureType = GetIndexReturnType(index: indexExpr);
            if (structureType != null)
            {
                return structureType;
            }
        }

        // First, check if the semantic analyzer has already resolved the type
        if (expr.ResolvedType != null)
        {
            // During monomorphization, resolve unsubstituted generic params (e.g., Snatched[U] → Snatched[S64])
            TypeInfo resolved = ApplyTypeSubstitutions(type: expr.ResolvedType);
            // If the type is still an unresolved generic parameter, fall through to the
            // expression-specific resolution which can use call-site type arguments
            if (resolved is not GenericParameterTypeInfo)
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
            CreatorExpression ctor => LookupTypeInCurrentModule(name: ctor.TypeName),
            BinaryExpression binary => GetBinaryExpressionType(binary: binary),
            ChainedComparisonExpression => _registry.LookupType(
                name: "Bool"), // Comparisons return Bool
            UnaryExpression unary => GetUnaryExpressionType(unary: unary),
            CallExpression call => GetCallReturnType(call: call),
            GenericMethodCallExpression generic =>
                GetGenericMethodCallReturnType(generic: generic),
            IndexExpression index => GetIndexReturnType(index: index),
            NamedArgumentExpression named => GetExpressionType(expr: named.Value),
            DictEntryLiteralExpression dictEntry => dictEntry.ResolvedType,
            ConditionalExpression cond => GetExpressionType(expr: cond.TrueExpression),
            GenericMemberExpression gme => GetGenericMemberExpressionType(gme: gme),
            _ => null
        };
    }

    /// <summary>
    /// Gets the return type of an index expression by looking up $getitem on the target type.
    /// </summary>
    private TypeInfo? GetUnaryExpressionType(UnaryExpression unary)
    {
        TypeInfo? operandType = GetExpressionType(expr: unary.Operand);
        if (unary.Operator == UnaryOperator.ForceUnwrap && operandType != null)
        {
            // Force-unwrap: return the value type inside the Maybe/Result/Lookup wrapper
            if (IsCarrierType(type: operandType) && operandType.TypeArguments is { Count: 1 })
            {
                return operandType.TypeArguments[index: 0];
            }
        }

        return operandType;
    }

    private TypeInfo? GetBinaryExpressionType(BinaryExpression binary)
    {
        return binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterEqual or BinaryOperator.And or BinaryOperator.Or
            or BinaryOperator.Identical or BinaryOperator.NotIdentical or BinaryOperator.In
            or BinaryOperator.NotIn
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
        if (objType == null)
        {
            return null;
        }

        // Refresh stale generic entity resolutions (same as GetMemberType).
        // EntityTypeInfo.CreateInstance uses cycle detection that returns a shell with empty
        // MemberVariables when recursion is detected. The shell has GenericDefinition set,
        // so we can refresh it from the definition with the same type arguments.
        if (objType is EntityTypeInfo
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef
            } staleEntity && staleEntity.TypeArguments != null)
        {
            var refreshed = genDef.CreateInstance(typeArguments: staleEntity.TypeArguments) as EntityTypeInfo;
            if (refreshed is { MemberVariables.Count: > 0 })
            {
                objType = refreshed;
            }
        }

        // Find the member variable
        IReadOnlyList<MemberVariableInfo>? memberVars = objType switch
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

        // The member's type has type arguments — the first one is the element type
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
    /// Gets the return type of an index expression by looking up $getitem on the target type.
    /// </summary>
    private TypeInfo? GetIndexReturnType(IndexExpression index)
    {
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        if (targetType == null)
        {
            return null;
        }

        // Look up $getitem on the target type (handles generics and protocols automatically)
        RoutineInfo? getItem = _registry.LookupMethod(type: targetType, methodName: "$getitem");

        if (getItem?.ReturnType == null)
        {
            return null;
        }

        // Substitute generic return type params with concrete types from the target.
        // Prefer target type arguments over _typeSubstitutions to avoid ambiguous param names
        // (e.g., List[BTreeListNode[S64]].$getitem returns T, but the outer T maps to S64 —
        //  the correct resolution is BTreeListNode[S64] from the list's type args, not S64).
        TypeInfo returnType = getItem.ReturnType;
        if (targetType.TypeArguments is { Count: > 0 } && getItem.OwnerType?.GenericParameters is
                { Count: > 0 })
        {
            // Map generic params to concrete args (e.g., T → BTreeListNode[S64] for List[BTreeListNode[S64]].$getitem)
            IReadOnlyList<string>? genParams = getItem.OwnerType.GenericParameters;
            for (int i = 0; i < genParams.Count && i < targetType.TypeArguments.Count; i++)
            {
                if (returnType.Name == genParams[index: i])
                {
                    return targetType.TypeArguments[index: i];
                }
            }
        }

        // Fallback: use _typeSubstitutions for simple generic params
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: returnType.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        return returnType;
    }

    /// <summary>
    /// Gets the return type of a call expression.
    /// </summary>
    private TypeInfo? GetCallReturnType(CallExpression call)
    {
        switch (call.Callee)
        {
            case MemberExpression member:
            {
                // Qualified method call: resolve receiver type, look up Type.method
                TypeInfo? receiverType = GetExpressionType(expr: member.Object);
                if (receiverType != null)
                {
                    RoutineInfo? method = _registry.LookupMethod(type: receiverType,
                        methodName: member.PropertyName);

                    if (method?.ReturnType != null)
                    {
                        // Substitute generic type params in return type (e.g., T → Character)
                        if (_typeSubstitutions != null &&
                            _typeSubstitutions.TryGetValue(key: method.ReturnType.Name,
                                value: out TypeInfo? sub))
                        {
                            return sub;
                        }

                        // For generic resolution receivers (e.g., Snatched[U8].read() → T should become U8),
                        // substitute using the receiver's type arguments when no _typeSubstitutions available
                        if (receiverType is
                                { IsGenericResolution: true, TypeArguments: not null } &&
                            method.ReturnType is GenericParameterTypeInfo)
                        {
                            // Find the generic parameter index in the owner type's generic parameters
                            TypeInfo? ownerGenericDef = receiverType switch
                            {
                                RecordTypeInfo r => r.GenericDefinition,
                                EntityTypeInfo e => e.GenericDefinition,

                                _ => null
                            };
                            if (ownerGenericDef?.GenericParameters != null)
                            {
                                int paramIndex = ownerGenericDef.GenericParameters
                                                                .ToList()
                                                                .IndexOf(item: method.ReturnType
                                                                    .Name);
                                if (paramIndex >= 0 &&
                                    paramIndex < receiverType.TypeArguments.Count)
                                {
                                    return receiverType.TypeArguments[index: paramIndex];
                                }
                            }
                        }

                        // For parameterized return types (e.g., Snatched[T] → Snatched[Character]),
                        // resolve through receiver's type arguments even without _typeSubstitutions
                        if (receiverType is
                                { IsGenericResolution: true, TypeArguments: not null } &&
                            method.ReturnType is
                                { IsGenericResolution: true, TypeArguments: not null })
                        {
                            TypeInfo? ownerGenericDef = receiverType switch
                            {
                                RecordTypeInfo r => r.GenericDefinition,
                                EntityTypeInfo e => e.GenericDefinition,

                                _ => null
                            };
                            if (ownerGenericDef?.GenericParameters != null)
                            {
                                var paramSubs = new Dictionary<string, TypeInfo>();
                                for (int i = 0;
                                     i < ownerGenericDef.GenericParameters.Count &&
                                     i < receiverType.TypeArguments.Count;
                                     i++)
                                {
                                    paramSubs[key: ownerGenericDef.GenericParameters[index: i]] =
                                        receiverType.TypeArguments[index: i];
                                }

                                bool anyResolved = false;
                                var resolvedArgs = new List<TypeInfo>();
                                foreach (TypeInfo ta in method.ReturnType.TypeArguments)
                                {
                                    if (paramSubs.TryGetValue(key: ta.Name,
                                            value: out TypeInfo? resolved))
                                    {
                                        resolvedArgs.Add(item: resolved);
                                        anyResolved = true;
                                    }
                                    else
                                    {
                                        resolvedArgs.Add(item: ta);
                                    }
                                }

                                if (anyResolved)
                                {
                                    string baseName = method.ReturnType.Name;
                                    int bracketIdx = baseName.IndexOf(value: '[');
                                    if (bracketIdx > 0)
                                    {
                                        baseName = baseName[..bracketIdx];
                                    }

                                    TypeInfo? genericDef = _registry.LookupType(name: baseName);
                                    if (genericDef != null)
                                    {
                                        return _registry.GetOrCreateResolution(
                                            genericDef: genericDef,
                                            typeArguments: resolvedArgs);
                                    }
                                }
                            }
                        }

                        // Fallback: substitute type arguments using _typeSubstitutions
                        if (_typeSubstitutions != null && method.ReturnType.IsGenericResolution &&
                            method.ReturnType.TypeArguments != null)
                        {
                            var substitutedArgs = new List<TypeInfo>();
                            bool anySubstituted = false;
                            foreach (TypeInfo typeArg in method.ReturnType.TypeArguments)
                            {
                                if (_typeSubstitutions.TryGetValue(key: typeArg.Name,
                                        value: out TypeInfo? resolvedArg))
                                {
                                    substitutedArgs.Add(item: resolvedArg);
                                    anySubstituted = true;
                                }
                                else
                                {
                                    substitutedArgs.Add(item: typeArg);
                                }
                            }

                            if (anySubstituted)
                            {
                                string baseName = method.ReturnType.Name;
                                int bracketIdx = baseName.IndexOf(value: '[');
                                if (bracketIdx > 0)
                                {
                                    baseName = baseName[..bracketIdx];
                                }

                                TypeInfo? genericDef = _registry.LookupType(name: baseName);
                                if (genericDef != null)
                                {
                                    return _registry.GetOrCreateResolution(genericDef: genericDef,
                                        typeArguments: substitutedArgs);
                                }
                            }
                        }

                        return method.IsAsync
                            ? WrapAsyncReturnType(method: method,
                                returnType: method.ReturnType)
                            : method.ReturnType;
                    }
                }

                // Representable pattern: obj.TypeName() → TypeName.$create(from: obj)
                // If the method name matches a registered type, the return type is that type.
                // Strip '!' suffix for failable conversions (e.g., index.U64!() → U64)
                string conversionLookup = member.PropertyName.EndsWith(value: '!')
                    ? member.PropertyName[..^1]
                    : member.PropertyName;
                TypeInfo? representableType = _registry.LookupType(name: conversionLookup);
                if (representableType != null)
                {
                    return representableType;
                }

                // Fall back to unqualified lookup
                RoutineInfo? fallback = _registry.LookupRoutine(fullName: member.PropertyName);
                return fallback?.ReturnType;
            }
            case IdentifierExpression id:
            {
                // Strip failable '!' suffix for lookup
                string callName = id.Name.EndsWith(value: '!')
                    ? id.Name[..^1]
                    : id.Name;
                // Try direct routine lookup first
                RoutineInfo? routine = _registry.LookupRoutine(fullName: callName) ??
                                       _registry.LookupRoutineByName(name: callName);
                if (routine?.ReturnType != null)
                {
                    return routine.IsAsync
                        ? WrapAsyncReturnType(method: routine, returnType: routine.ReturnType)
                        : routine.ReturnType;
                }

                // If name matches a type, it's a creator call — returns that type
                TypeInfo? calledType = LookupTypeInCurrentModule(name: id.Name);
                if (calledType != null)
                {
                    RoutineInfo? creator =
                        _registry.LookupMethod(type: calledType, methodName: "$create");
                    return creator?.ReturnType ?? calledType;
                }

                return null;
            }
            default:
                return null;
        }
    }

    private TypeInfo? WrapAsyncReturnType(RoutineInfo method, TypeInfo? returnType)
    {
        if (!method.IsAsync || returnType == null)
        {
            return returnType;
        }

        TypeInfo? taskDef = LookupTypeInCurrentModule(name: "Task");
        if (taskDef is { IsGenericDefinition: true })
        {
            return _registry.GetOrCreateResolution(genericDef: taskDef, typeArguments: [returnType]);
        }

        return returnType;
    }

    /// <summary>
    /// Gets the type of a literal expression from its token type.
    /// </summary>
    private TypeInfo? GetLiteralType(LiteralExpression literal)
    {
        string? typeName = literal.LiteralType switch
        {
            Lexer.TokenType.S8Literal => "S8",
            Lexer.TokenType.S16Literal => "S16",
            Lexer.TokenType.S32Literal => "S32",
            Lexer.TokenType.S64Literal => "S64",
            Lexer.TokenType.S128Literal => "S128",
            Lexer.TokenType.U8Literal => "U8",
            Lexer.TokenType.U16Literal => "U16",
            Lexer.TokenType.U32Literal => "U32",
            Lexer.TokenType.U64Literal => "U64",
            Lexer.TokenType.U128Literal => "U128",
            Lexer.TokenType.F16Literal => "F16",
            Lexer.TokenType.F32Literal => "F32",
            Lexer.TokenType.F64Literal => "F64",
            Lexer.TokenType.F128Literal => "F128",
            Lexer.TokenType.D32Literal => "D32",
            Lexer.TokenType.D64Literal => "D64",
            Lexer.TokenType.D128Literal => "D128",
            Lexer.TokenType.True or Lexer.TokenType.False => "Bool",
            Lexer.TokenType.TextLiteral => "Text",
            Lexer.TokenType.CharacterLiteral => "Character",
            Lexer.TokenType.ByteLetterLiteral => "Byte",
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
        // Fallback: if SA didn't set ResolvedType (type-as-identifier), try type lookup by name
        if (targetType == null && member.Object is IdentifierExpression typeId)
        {
            targetType = LookupTypeInCurrentModule(name: typeId.Name);
        }

        if (targetType == null)
        {
            return null;
        }

        // Refresh stale generic entity resolutions for member variable lookup
        if (targetType is EntityTypeInfo
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef
            } staleEntity && staleEntity.TypeArguments != null)
        {
            var refreshed =
                genDef.CreateInstance(typeArguments: staleEntity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Count > 0)
            {
                targetType = refreshed;
            }
        }

        // Choice/Flags member access returns the type itself
        if (targetType is ChoiceTypeInfo or FlagsTypeInfo)
        {
            return targetType;
        }

        MemberVariableInfo? memberVariable = targetType switch
        {
            EntityTypeInfo e => e.LookupMemberVariable(memberVariableName: member.PropertyName),
            RecordTypeInfo r => r.LookupMemberVariable(memberVariableName: member.PropertyName),
            _ => null
        };

        TypeInfo? memberType = memberVariable?.Type;
        if (memberType != null && targetType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            memberType = ResolveGenericMemberType(memberType: memberType, ownerType: targetType);
        }

        return memberType;
    }


    /// <summary>
    /// Gets the bit width of an LLVM type.
    /// </summary>
    /// <summary>
    /// Gets the element type from a List[T] entity by parsing the type parameter.
    /// </summary>
    private TypeInfo? GetListElementType(EntityTypeInfo listEntity)
    {
        string name = listEntity.Name;
        int bracketStart = name.IndexOf(value: '[');
        int bracketEnd = name.LastIndexOf(value: ']');
        if (bracketStart < 0 || bracketEnd <= bracketStart)
        {
            return null;
        }

        string elemTypeName = name[(bracketStart + 1)..bracketEnd];
        return _registry.LookupType(name: elemTypeName);
    }

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


}
