namespace Compiler.CodeGen;

using System.Text;
using TypeModel.Symbols;
using SyntaxTree;

/// <summary>
/// LLVM intrinsic call emission — template-based IR generation for
/// routines annotated with <c>@llvm_ir("...")</c>.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Emits a call to an LLVM intrinsic routine using its <c>@llvm_ir</c> template.
    /// Called from <see cref="EmitFunctionCall"/> and <see cref="EmitMethodCall"/> when
    /// <c>resolvedRoutine.LlvmIrTemplate != null</c>.
    /// </summary>
    private string EmitLlvmIntrinsicCall(StringBuilder sb, RoutineInfo routine,
        string? receiver, List<Expression> arguments,
        IReadOnlyList<TypeExpression>? typeArguments)
    {
        // Emit argument values.
        var argValues = new List<string>();
        if (receiver != null)
            argValues.Add(receiver);
        foreach (Expression arg in arguments)
            argValues.Add(EmitExpression(sb: sb, expr: arg));

        // Resolve type arguments to LLVM type strings.
        var llvmTypeArgs = new List<string>();
        if (typeArguments != null)
        {
            foreach (TypeExpression ta in typeArguments)
                llvmTypeArgs.Add(ResolveTypeExpressionToLlvm(typeExpr: ta));
        }

        string mold = routine.LlvmIrTemplate!;
        return EmitFromTemplate(sb: sb, mold: mold, method: routine,
            llvmTypeArgs: llvmTypeArgs, args: argValues);
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with <c>{hole}</c> substitution.
    /// Supports multi-line templates (for overflow intrinsics, alloca/GEP patterns, etc.).
    /// </summary>
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
            if (method.GenericParameters != null)
            {
                for (int i = 0; i < method.GenericParameters.Count && i < llvmTypeArgs.Count; i++)
                {
                    string paramName = method.GenericParameters[index: i];
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

            EmitLine(sb: sb, line: $"  {substituted}");

            if (hasResult)
            {
                firstResult ??= currentResult;
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        return lastResult ?? (args.Count > 0 ? args[index: 0] : "undef");
    }

    /// <summary>
    /// Resolves a <see cref="TypeExpression"/> to its LLVM type string,
    /// applying active type substitutions and registry lookups.
    /// </summary>
    private string ResolveTypeExpressionToLlvm(TypeExpression typeExpr)
    {
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: typeExpr.Name, value: out var sub))
            return GetLlvmType(type: sub);

        var type = _registry.LookupType(name: typeExpr.Name);
        if (type != null)
        {
            if (type.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 })
            {
                string fullName =
                    $"{typeExpr.Name}[{string.Join(separator: ", ", values: typeExpr.GenericArguments.Select(selector: g => g.Name))}]";
                var fullType = _registry.LookupType(name: fullName);
                if (fullType != null) return GetLlvmType(type: fullType);

                var resolvedArgs = new System.Collections.Generic.List<TypeModel.Types.TypeInfo>();
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

        if (_typeSubstitutions != null)
        {
            foreach (var sub2 in _typeSubstitutions.Values)
            {
                if (sub2.Name == typeExpr.Name) return GetLlvmType(type: sub2);
            }
        }

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
                arguments: gmc.Arguments, typeArguments: gmc.TypeArguments);

        string objectDesc = gmc.Object is IdentifierExpression id2 ? id2.Name : gmc.Object.GetType().Name;
        throw new InvalidOperationException(
            $"GenericMethodCallExpression reached codegen — GenericCallLoweringPass must lower all GMCEs to CallExpression before codegen. " +
            $"GMCE: {objectDesc}.{gmc.MethodName}[{string.Join(", ", gmc.TypeArguments?.Select(t => t.Name) ?? [])}], " +
            $"in routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})");
    }
}