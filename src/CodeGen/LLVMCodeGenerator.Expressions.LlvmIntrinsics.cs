using System.Text;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// LLVM intrinsic call emission — template-based IR generation for
/// routines annotated with <c>@llvm_ir("...")</c>.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Emits a call to an LLVM intrinsic routine using its <c>@llvm_ir</c> template.
    /// Called from <see cref="EmitRoutineCall"/> and <see cref="EmitMemberRoutineCall"/> when
    /// <c>resolvedRoutine.LlvmIrTemplate != null</c>.
    /// </summary>
    private string EmitLlvmIntrinsicCall(StringBuilder sb, RoutineInfo routine,
        string? receiver, List<Expression> arguments,
        IReadOnlyList<TypeExpression>? typeArguments,
        TypeInfo? resolvedReturnType = null)
    {
        // Emit argument values.
        var argValues = new List<string>();
        if (receiver != null)
            argValues.Add(receiver);
        foreach (Expression arg in arguments)
            argValues.Add(EmitExpression(sb: sb, expr: arg));

        // Resolve type arguments to LLVM type strings.
        IReadOnlyList<string>? genericParameters =
            routine.GenericParameters ?? routine.GenericDefinition?.GenericParameters;
        var llvmTypeArgs = new List<string>();
        if (typeArguments != null)
        {
            foreach (TypeExpression ta in typeArguments)
                llvmTypeArgs.Add(ResolveTypeExpressionToLlvm(typeExpr: ta));
        }

        List<string> inferredTypeArgs = InferLlvmIntrinsicTypeArguments(routine: routine,
            arguments: arguments,
            resolvedReturnType: resolvedReturnType);

        if (typeArguments == null)
        {
            llvmTypeArgs.AddRange(inferredTypeArgs);
        }
        else if (inferredTypeArgs.Count == llvmTypeArgs.Count)
        {
            for (int i = 0; i < llvmTypeArgs.Count; i++)
            {
                bool unresolvedExplicit = !LooksLikeLlvmType(llvmTypeArgs[i]);
                bool namedGenericMatch =
                    genericParameters is { Count: > 0 } &&
                    i < genericParameters.Count &&
                    llvmTypeArgs[i] == genericParameters[i];
                if (unresolvedExplicit || namedGenericMatch)
                {
                    llvmTypeArgs[i] = inferredTypeArgs[i];
                }
            }
        }

        string mold = routine.LlvmIrTemplate!;
        return EmitFromTemplate(sb: sb, mold: mold, method: routine,
            llvmTypeArgs: llvmTypeArgs, args: argValues);
    }

    private List<string> InferLlvmIntrinsicTypeArguments(RoutineInfo routine,
        IReadOnlyList<Expression> arguments, TypeInfo? resolvedReturnType)
    {
        if (routine.TypeArguments is { Count: > 0 })
        {
            return routine.TypeArguments.Select(selector: GetLlvmType).ToList();
        }

        IReadOnlyList<string>? genericParameters =
            routine.GenericParameters ?? routine.GenericDefinition?.GenericParameters;
        if (genericParameters is not { Count: > 0 })
        {
            return [];
        }

        var inferred = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        var inferredLlvmTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < routine.Parameters.Count && i < arguments.Count; i++)
        {
            TypeInfo? argType = GetExpressionType(expr: arguments[i]);
            if (argType != null)
            {
                InferGenericBindings(pattern: routine.Parameters[i].Type,
                    concrete: argType,
                    inferred: inferred);
            }

            InferGenericLlvmBindings(pattern: routine.Parameters[i].Type,
                concreteLlvmType: GetExpressionLlvmType(expr: arguments[i]),
                inferredLlvmTypes: inferredLlvmTypes);
        }

        if (resolvedReturnType != null && routine.ReturnType != null
            && resolvedReturnType is not GenericParameterTypeInfo)
        {
            InferGenericBindings(pattern: routine.ReturnType,
                concrete: resolvedReturnType,
                inferred: inferred);
            InferGenericLlvmBindings(pattern: routine.ReturnType,
                concreteLlvmType: GetLlvmType(type: resolvedReturnType),
                inferredLlvmTypes: inferredLlvmTypes);
        }

        var llvmTypeArgs = new List<string>(capacity: genericParameters.Count);
        foreach (string genericParam in genericParameters)
        {
            if (inferred.TryGetValue(key: genericParam, value: out TypeInfo? concreteType))
            {
                llvmTypeArgs.Add(GetLlvmType(type: concreteType));
                continue;
            }

            if (inferredLlvmTypes.TryGetValue(key: genericParam, value: out string? concreteLlvmType))
            {
                llvmTypeArgs.Add(concreteLlvmType);
                continue;
            }

            return [];
        }

        return llvmTypeArgs;
    }

    private static void InferGenericBindings(TypeInfo pattern, TypeInfo concrete,
        Dictionary<string, TypeInfo> inferred)
    {
        if (pattern is GenericParameterTypeInfo genericParam)
        {
            inferred.TryAdd(key: genericParam.Name, value: concrete);
            return;
        }

        if (pattern is RoutineTypeInfo patternRoutine && concrete is RoutineTypeInfo concreteRoutine)
        {
            for (int i = 0;
                 i < patternRoutine.ParameterTypes.Count && i < concreteRoutine.ParameterTypes.Count;
                 i++)
            {
                InferGenericBindings(pattern: patternRoutine.ParameterTypes[i],
                    concrete: concreteRoutine.ParameterTypes[i],
                    inferred: inferred);
            }

            if (patternRoutine.ReturnType != null && concreteRoutine.ReturnType != null)
            {
                InferGenericBindings(pattern: patternRoutine.ReturnType,
                    concrete: concreteRoutine.ReturnType,
                    inferred: inferred);
            }
            return;
        }

        if (pattern is TupleTypeInfo patternTuple && concrete is TupleTypeInfo concreteTuple)
        {
            for (int i = 0; i < patternTuple.ElementTypes.Count && i < concreteTuple.ElementTypes.Count; i++)
            {
                InferGenericBindings(pattern: patternTuple.ElementTypes[i],
                    concrete: concreteTuple.ElementTypes[i],
                    inferred: inferred);
            }
            return;
        }

        if (pattern.TypeArguments is not { Count: > 0 } || concrete.TypeArguments is not { Count: > 0 })
        {
            return;
        }

        for (int i = 0; i < pattern.TypeArguments.Count && i < concrete.TypeArguments.Count; i++)
        {
            InferGenericBindings(pattern: pattern.TypeArguments[i],
                concrete: concrete.TypeArguments[i],
                inferred: inferred);
        }
    }

    private static void InferGenericLlvmBindings(TypeInfo pattern, string concreteLlvmType,
        Dictionary<string, string> inferredLlvmTypes)
    {
        if (pattern is GenericParameterTypeInfo genericParam)
        {
            inferredLlvmTypes.TryAdd(key: genericParam.Name, value: concreteLlvmType);
        }
    }

    private static bool LooksLikeLlvmType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value == "void" ||
               value == "ptr" ||
               value == "half" ||
               value == "float" ||
               value == "double" ||
               value == "fp128" ||
               value.StartsWith(value: "i", comparisonType: StringComparison.Ordinal) &&
               value.Length > 1 &&
               char.IsDigit(value[1]) ||
               value.StartsWith(value: "%", comparisonType: StringComparison.Ordinal) ||
               value.StartsWith(value: "{", comparisonType: StringComparison.Ordinal) ||
               value.StartsWith(value: "[", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with <c>{hole}</c> substitution.
    /// Supports multi-line templates (for overflow intrinsics, alloca/GEP patterns, etc.).
    /// </summary>
    /// LLVM rejects bitcast between integer and pointer types. The reinterpret_bits intrinsic
    /// template emits `bitcast {From} {value} to {To}`, which is invalid when one side is `ptr`
    /// and the other is `iN`. Rewrite those cases to use `inttoptr` / `ptrtoint`.
    private static string FixIntPtrBitcast(string line)
    {
        const string marker = "= bitcast ";
        int idx = line.IndexOf(value: marker, comparisonType: StringComparison.Ordinal);
        if (idx < 0) return line;
        int valueStart = idx + marker.Length;
        int toIdx = line.IndexOf(value: " to ", startIndex: valueStart,
            comparisonType: StringComparison.Ordinal);
        if (toIdx < 0) return line;
        string fromType = line.Substring(startIndex: valueStart, length: toIdx - valueStart)
            .Split(separator: ' ')[0];
        string toType = line.Substring(startIndex: toIdx + 4).Trim();
        bool fromIsInt = fromType.Length > 1 && fromType[0] == 'i' && char.IsDigit(c: fromType[1]);
        bool toIsInt = toType.Length > 1 && toType[0] == 'i' && char.IsDigit(c: toType[1]);
        bool fromIsPtr = fromType == "ptr";
        bool toIsPtr = toType == "ptr";
        if (fromIsInt && toIsPtr)
            return line.Substring(startIndex: 0, length: idx + 2) + "inttoptr " +
                   line.Substring(startIndex: valueStart);
        if (fromIsPtr && toIsInt)
            return line.Substring(startIndex: 0, length: idx + 2) + "ptrtoint " +
                   line.Substring(startIndex: valueStart);
        return line;
    }

    private string EmitFromTemplate(StringBuilder sb, string mold, RoutineInfo method,
        List<string> llvmTypeArgs, List<string> args)
    {
        string[] lines = mold.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);
        string? lastResult = null;
        string? prevResult = null;
        string? firstResult = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            string currentResult = NextTemp();
            bool hasResult = line.Contains(value: "{result}");

            string substituted = line;
            substituted = substituted.Replace(oldValue: "{result}", newValue: currentResult);

            if (prevResult != null)
                substituted = substituted.Replace(oldValue: "{prev}", newValue: prevResult);

            if (firstResult != null)
                substituted = substituted.Replace(oldValue: "{first}", newValue: firstResult);

            // {T}, {From}, {To}, etc. — named generic parameters → LLVM types
            IReadOnlyList<string>? genericParameters =
                method.GenericParameters ?? method.GenericDefinition?.GenericParameters;
            if (genericParameters != null)
            {
                for (int i = 0; i < genericParameters.Count && i < llvmTypeArgs.Count; i++)
                {
                    string paramName = genericParameters[index: i];
                    substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                        newValue: llvmTypeArgs[index: i]);

                    string sizeofPattern = $"{{sizeof {paramName}}}";
                    if (substituted.Contains(value: sizeofPattern))
                    {
                        substituted = substituted.Replace(oldValue: sizeofPattern,
                            newValue: (GetTypeBitWidth(llvmType: llvmTypeArgs[index: i]) / 8)
                                      .ToString());
                    }
                }
            }

            // {paramName} → emitted arg value (positional by parameter list order)
            for (int i = 0; i < method.Parameters.Count && i < args.Count; i++)
            {
                string paramName = method.Parameters[index: i].Name;
                substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                    newValue: args[index: i]);
            }

            substituted = FixIntPtrBitcast(line: substituted);

            EmitLine(sb: sb, line: $"  {substituted}");

            if (hasResult)
            {
                firstResult ??= currentResult;
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        // Overflow intrinsics return anonymous struct types like { i128, i1 }.
        // If the method's return type is a TupleTypeInfo, coerce via extractvalue/insertvalue
        // so the caller receives the named LLVM type (%"Record.Tuple[...]").
        if (lastResult != null && method.ReturnType is TupleTypeInfo tupleReturn)
        {
            string namedType = GetLlvmType(type: tupleReturn);
            string anonType =
                $"{{ {string.Join(separator: ", ", values: tupleReturn.ElementTypes.Select(selector: GetLlvmType))} }}";
            string tupleVal = "undef";
            for (int i = 0; i < tupleReturn.ElementTypes.Count; i++)
            {
                string elem = NextTemp();
                EmitLine(sb: sb, line: $"  {elem} = extractvalue {anonType} {lastResult}, {i}");
                string ins = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {ins} = insertvalue {namedType} {tupleVal}, {GetLlvmType(type: tupleReturn.ElementTypes[index: i])} {elem}, {i}");
                tupleVal = ins;
            }
            return tupleVal;
        }

        return lastResult ?? (args.Count > 0 ? args[index: 0] : "undef");
    }

    /// <summary>
    /// Resolves a <see cref="TypeExpression"/> to its LLVM type string,
    /// applying active type substitutions and registry lookups.
    /// </summary>
    private string ResolveTypeExpressionToLlvm(TypeExpression typeExpr)
    {
        if (typeExpr.ResolvedType is { } resolvedType && resolvedType is not ErrorTypeInfo)
        {
            return GetLlvmType(type: ApplyTypeSubstitutions(type: resolvedType));
        }

        var type = _registry.LookupType(name: typeExpr.Name);
        if (type != null)
        {
            if (type.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 })
            {
                string fullName =
                    $"{typeExpr.Name}[{string.Join(separator: ", ", values: typeExpr.GenericArguments.Select(selector: g => g.Name))}]";
                var fullType = _registry.LookupType(name: fullName);
                if (fullType != null) return GetLlvmType(type: fullType);

                var resolvedArgs = new List<TypeInfo>();
                foreach (TypeExpression ga in typeExpr.GenericArguments)
                {
                    var r = ResolveTypeArgument(ta: ga);
                    if (r != null) resolvedArgs.Add(r);
                }
                if (resolvedArgs.Count == type.GenericParameters!.Count)
                    return GetLlvmType(type: _registry.GetOrCreateResolution(
                        genericDef: type, typeArguments: resolvedArgs));
            }
            return GetLlvmType(type: type);
        }

        type = LookupTypeInCurrentModule(name: typeExpr.Name);
        if (type != null) return GetLlvmType(type: type);

        return typeExpr.Name;
    }

    /// <summary>
    /// Fallback handler for <see cref="GenericMethodCallExpression"/> nodes that reached codegen
    /// without being lowered. Handles LLVM intrinsic free-function GMCEs by looking up the routine
    /// in the registry. Throws for any non-intrinsic GMCE (contract violation).
    /// </summary>
    private string EmitGmceFallback(StringBuilder sb, GenericMethodCallExpression gmc)
    {
        RoutineInfo? routine = gmc.ResolvedRoutine;

        // Try registry lookup for unresolved free-function calls (Object.Name == MethodName).
        if (routine == null && gmc.Object is IdentifierExpression freeId &&
            freeId.Name == gmc.MethodName)
        {
            routine = _registry.LookupRoutineByName(name: gmc.MethodName);
        }

        if (routine?.LlvmIrTemplate != null)
            return EmitLlvmIntrinsicCall(sb: sb, routine: routine, receiver: null,
                arguments: gmc.Arguments, typeArguments: gmc.TypeArguments,
                resolvedReturnType: gmc.ResolvedType);

        string objectDesc = gmc.Object is IdentifierExpression id2 ? id2.Name : gmc.Object.GetType().Name;
        throw new InvalidOperationException(
            $"GenericMethodCallExpression reached codegen — GenericCallLoweringPass must lower all GMCEs to CallExpression before codegen. " +
            $"GMCE: {objectDesc}.{gmc.MethodName}[{string.Join(", ", gmc.TypeArguments?.Select(t => t.Name) ?? [])}], " +
            $"in routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})");
    }
}
