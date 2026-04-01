namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for inserted text and represent/diagnose conversion.
/// </summary>
public partial class LLVMCodeGenerator
{
    private string EmitInsertedText(StringBuilder sb, InsertedTextExpression inserted)
    {
        if (inserted.Parts.Count == 0)
        {
            return EmitStringLiteral(sb: sb, value: "");
        }

        // Convert each part to a ptr (Text value)
        var partValues = new List<string>();
        foreach (InsertedTextPart part in inserted.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    partValues.Add(item: EmitStringLiteral(sb: sb, value: textPart.Text));
                    break;
                case ExpressionPart exprPart:
                    partValues.Add(item: EmitInsertedTextPart(sb: sb, exprPart: exprPart));
                    break;
            }
        }

        if (partValues.Count == 1)
        {
            return partValues[index: 0];
        }

        // Chain concat calls via native rf_text_concat
        if (_declaredNativeFunctions.Add(item: "rf_text_concat"))
        {
            EmitLine(sb: _functionDeclarations, line: "declare ptr @rf_text_concat(ptr, ptr)");
        }

        string accumulator = partValues[index: 0];
        for (int i = 1; i < partValues.Count; i++)
        {
            string concatResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {concatResult} = call ptr @rf_text_concat(ptr {accumulator}, ptr {partValues[index: i]})");
            accumulator = concatResult;
        }

        return accumulator;
    }

    /// <summary>
    /// Emits a single expression part of an f-string, handling format specifiers.
    /// Valid specifiers: null (default �� $represent), "=" (name prefix + $represent),
    /// "?" ($diagnose), "=?" (name prefix + $diagnose).
    /// </summary>
    private string EmitInsertedTextPart(StringBuilder sb, ExpressionPart exprPart)
    {
        string exprValue = EmitExpression(sb: sb, expr: exprPart.Expression);
        TypeInfo? exprType = GetExpressionType(expr: exprPart.Expression);
        string? formatSpec = exprPart.FormatSpec;

        bool hasName = formatSpec != null && formatSpec.Contains(value: '=');
        bool hasDiagnose = formatSpec != null && formatSpec.Contains(value: '?');

        // Resolve the text value via $diagnose or $represent
        string valueText = hasDiagnose
            ? EmitDiagnoseCall(sb: sb, value: exprValue, type: exprType)
            : EmitRepresentCall(sb: sb, value: exprValue, type: exprType);

        // Prepend "name=" prefix if = specifier is present
        if (hasName)
        {
            string varName = exprPart.Expression is IdentifierExpression id
                ? id.Name
                : "expr";
            string prefix = EmitStringLiteral(sb: sb, value: $"{varName}=");

            if (_declaredNativeFunctions.Add(item: "rf_text_concat"))
            {
                EmitLine(sb: _functionDeclarations, line: "declare ptr @rf_text_concat(ptr, ptr)");
            }

            string result = NextTemp();
            EmitLine(sb: sb,
                line: $"  {result} = call ptr @rf_text_concat(ptr {prefix}, ptr {valueText})");
            return result;
        }

        return valueText;
    }

    /// <summary>
    /// Emits a call to Type.$represent() to convert a value to Text.
    /// </summary>
    private string EmitRepresentCall(StringBuilder sb, string value, TypeInfo? type)
    {
        // If already Text, return directly
        if (type?.Name == "Text")
        {
            return value;
        }

        string typeName = type?.Name ?? "Data";
        string mangledName;
        RoutineInfo? representMethod = null;

        if (type != null)
        {
            ResolvedMethod? resolved = ResolveMethod(receiverType: type, methodName: "$represent");
            if (resolved != null)
            {
                representMethod = resolved.Routine;
                mangledName = resolved.MangledName;

                // For monomorphized methods on generic definitions, use the resolved type name
                if (representMethod.IsGenericDefinition && !resolved.IsMonomorphized &&
                    typeName != representMethod.OwnerType?.Name)
                {
                    string module = representMethod.OwnerType?.Module ?? representMethod.Module ?? "";
                    string prefix = module != "" ? $"{module}." : "";
                    mangledName = Q(name: $"{prefix}{typeName}.$represent");
                    if (type.IsGenericResolution)
                    {
                        RecordMonomorphization(mangledName: mangledName,
                            genericMethod: representMethod,
                            resolvedOwnerType: type);
                    }
                }

                GenerateFunctionDeclaration(routine: representMethod);
            }
            else
            {
                mangledName = Q(name: $"{typeName}.$represent");
            }
        }
        else
        {
            representMethod = _registry.LookupRoutine(fullName: $"{typeName}.$represent");
            if (representMethod != null)
            {
                GenerateFunctionDeclaration(routine: representMethod);
            }
            mangledName = representMethod != null
                ? MangleFunctionName(routine: representMethod)
                : Q(name: $"{typeName}.$represent");
        }

        string argType = type != null
            ? GetParameterLLVMType(type: type)
            : "i64";
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = call ptr @{mangledName}({argType} {value})");
        return result;
    }

    /// <summary>
    /// Emits a call to Type.$diagnose() to convert a value to its diagnostic Text.
    /// </summary>
    private string EmitDiagnoseCall(StringBuilder sb, string value, TypeInfo? type)
    {
        string typeName = type?.Name ?? "Data";
        string mangledName;
        RoutineInfo? diagnoseMethod = null;

        if (type != null)
        {
            ResolvedMethod? resolved = ResolveMethod(receiverType: type, methodName: "$diagnose");
            if (resolved != null)
            {
                diagnoseMethod = resolved.Routine;
                mangledName = resolved.MangledName;

                // For monomorphized methods on generic definitions, use the resolved type name
                if (diagnoseMethod.IsGenericDefinition && !resolved.IsMonomorphized &&
                    typeName != diagnoseMethod.OwnerType?.Name)
                {
                    string module = diagnoseMethod.OwnerType?.Module ?? diagnoseMethod.Module ?? "";
                    string prefix = module != "" ? $"{module}." : "";
                    mangledName = Q(name: $"{prefix}{typeName}.$diagnose");
                    if (type.IsGenericResolution)
                    {
                        RecordMonomorphization(mangledName: mangledName,
                            genericMethod: diagnoseMethod,
                            resolvedOwnerType: type);
                    }
                }

                GenerateFunctionDeclaration(routine: diagnoseMethod);
            }
            else
            {
                mangledName = Q(name: $"{typeName}.$diagnose");
            }
        }
        else
        {
            diagnoseMethod = _registry.LookupRoutine(fullName: $"{typeName}.$diagnose");
            if (diagnoseMethod != null)
            {
                GenerateFunctionDeclaration(routine: diagnoseMethod);
            }
            mangledName = diagnoseMethod != null
                ? MangleFunctionName(routine: diagnoseMethod)
                : Q(name: $"{typeName}.$diagnose");
        }

        string argType = type != null
            ? GetParameterLLVMType(type: type)
            : "i64";
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = call ptr @{mangledName}({argType} {value})");
        return result;
    }
}
