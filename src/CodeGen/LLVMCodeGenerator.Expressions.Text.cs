namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
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
            return EmitStringLiteral(sb, "");
        }

        // Convert each part to a ptr (Text value)
        var partValues = new List<string>();
        foreach (var part in inserted.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    partValues.Add(EmitStringLiteral(sb, textPart.Text));
                    break;
                case ExpressionPart exprPart:
                    partValues.Add(EmitInsertedTextPart(sb, exprPart));
                    break;
            }
        }

        if (partValues.Count == 1)
        {
            return partValues[0];
        }

        // Chain concat calls via native rf_text_concat
        if (_declaredNativeFunctions.Add("rf_text_concat"))
            EmitLine(_functionDeclarations, "declare ptr @rf_text_concat(ptr, ptr)");

        string accumulator = partValues[0];
        for (int i = 1; i < partValues.Count; i++)
        {
            string concatResult = NextTemp();
            EmitLine(sb, $"  {concatResult} = call ptr @rf_text_concat(ptr {accumulator}, ptr {partValues[i]})");
            accumulator = concatResult;
        }

        return accumulator;
    }

    /// <summary>
    /// Emits a single expression part of an f-string, handling format specifiers.
    /// Valid specifiers: null (default → $represent), "=" (name prefix + $represent),
    /// "?" ($diagnose), "=?" (name prefix + $diagnose).
    /// </summary>
    private string EmitInsertedTextPart(StringBuilder sb, ExpressionPart exprPart)
    {
        string exprValue = EmitExpression(sb, exprPart.Expression);
        TypeInfo? exprType = GetExpressionType(exprPart.Expression);
        string? formatSpec = exprPart.FormatSpec;

        bool hasName = formatSpec != null && formatSpec.Contains('=');
        bool hasDiagnose = formatSpec != null && formatSpec.Contains('?');

        // Resolve the text value via $diagnose or $represent
        string valueText = hasDiagnose
            ? EmitDiagnoseCall(sb, exprValue, exprType)
            : EmitRepresentCall(sb, exprValue, exprType);

        // Prepend "name=" prefix if = specifier is present
        if (hasName)
        {
            string varName = exprPart.Expression is IdentifierExpression id ? id.Name : "expr";
            string prefix = EmitStringLiteral(sb, $"{varName}=");

            if (_declaredNativeFunctions.Add("rf_text_concat"))
                EmitLine(_functionDeclarations, "declare ptr @rf_text_concat(ptr, ptr)");
            string result = NextTemp();
            EmitLine(sb, $"  {result} = call ptr @rf_text_concat(ptr {prefix}, ptr {valueText})");
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
            return value;

        string typeName = type?.Name ?? "Data";
        string representName = $"{typeName}.$represent";
        RoutineInfo? representMethod = _registry.LookupRoutine(representName);
        // For generic resolutions (e.g., ValueBitList[8]), try the generic base name
        if (representMethod == null && type != null && GetGenericBaseName(type) is { } repBaseName)
        {
            representMethod = _registry.LookupRoutine($"{repBaseName}.$represent");
        }
        if (representMethod != null)
            GenerateFunctionDeclaration(representMethod);

        string mangledName = representMethod != null
            ? MangleFunctionName(representMethod)
            : Q($"{typeName}.$represent");

        // For monomorphized methods, use the resolved type name in the mangled function name
        if (representMethod != null && representMethod.IsGenericDefinition && typeName != representMethod.OwnerType?.Name)
        {
            string module = representMethod.OwnerType?.Module ?? representMethod.Module ?? "";
            string prefix = module != "" ? $"{module}." : "";
            mangledName = Q($"{prefix}{typeName}.$represent");
        }

        // Ensure the monomorphized body is compiled for generic types
        if (type != null && representMethod != null && type.IsGenericResolution)
            RecordMonomorphization(mangledName, representMethod, type);

        string argType = type != null ? GetParameterLLVMType(type) : "i64";
        string result = NextTemp();
        EmitLine(sb, $"  {result} = call ptr @{mangledName}({argType} {value})");
        return result;
    }

    /// <summary>
    /// Emits a call to Type.$diagnose() to convert a value to its diagnostic Text.
    /// </summary>
    private string EmitDiagnoseCall(StringBuilder sb, string value, TypeInfo? type)
    {
        string typeName = type?.Name ?? "Data";
        string diagnoseName = $"{typeName}.$diagnose";
        RoutineInfo? diagnoseMethod = _registry.LookupRoutine(diagnoseName);
        // For generic resolutions (e.g., ValueBitList[8]), try the generic base name
        if (diagnoseMethod == null && type != null && GetGenericBaseName(type) is { } diagBaseName)
        {
            diagnoseMethod = _registry.LookupRoutine($"{diagBaseName}.$diagnose");
        }
        if (diagnoseMethod != null)
            GenerateFunctionDeclaration(diagnoseMethod);

        string mangledName = diagnoseMethod != null
            ? MangleFunctionName(diagnoseMethod)
            : Q($"{typeName}.$diagnose");

        // For monomorphized methods, use the resolved type name in the mangled function name
        if (diagnoseMethod != null && diagnoseMethod.IsGenericDefinition && typeName != diagnoseMethod.OwnerType?.Name)
        {
            string module = diagnoseMethod.OwnerType?.Module ?? diagnoseMethod.Module ?? "";
            string prefix = module != "" ? $"{module}." : "";
            mangledName = Q($"{prefix}{typeName}.$diagnose");
        }

        // Ensure the monomorphized body is compiled for generic types
        if (type != null && diagnoseMethod != null && type.IsGenericResolution)
            RecordMonomorphization(mangledName, diagnoseMethod, type);

        string argType = type != null ? GetParameterLLVMType(type) : "i64";
        string result = NextTemp();
        EmitLine(sb, $"  {result} = call ptr @{mangledName}({argType} {value})");
        return result;
    }

}

