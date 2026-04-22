namespace Compiler.CodeGen;

using TypeModel.Symbols;
using TypeModel.Types;

/// <summary>
/// Declaration code generation for synthesized text conversion and hashing routines.
/// </summary>
public partial class LlvmCodeGenerator
{
    private void EmitSynthesizedEq(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLlvmType(type: routine.OwnerType);

        // Blank (void) has no LLVM representation — trivially equal (only one possible value)
        if (meType == "void")
        {
            EmitLine(sb: _functionDefinitions, line: $"define i1 @{funcName}() {{");
            EmitLine(sb: _functionDefinitions, line: "entry:");
            EmitLine(sb: _functionDefinitions, line: "  ret i1 true");
            EmitLine(sb: _functionDefinitions, line: "}");
            EmitLine(sb: _functionDefinitions, line: "");
            return;
        }

        string youType = routine.Parameters.Count > 0
            ? GetParameterLlvmType(type: routine.Parameters[index: 0].Type)
            : meType;
        string youName = routine.Parameters.Count > 0
            ? routine.Parameters[index: 0].Name
            : "you";

        EmitLine(sb: _functionDefinitions,
            line: $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        // Reset temp counter for clean output
        int savedTempCounter = _tempCounter;

        switch (routine.OwnerType)
        {
            case RecordTypeInfo { HasDirectBackendType: true } record:
            {
                // Single backend type: direct icmp
                string llvmType = record.LlvmType;
                string result = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line: $"  {result} = icmp eq {llvmType} %me, %{youName}");
                EmitLine(sb: _functionDefinitions, line: $"  ret i1 {result}");
                break;
            }
            case EntityTypeInfo entity when entity.MemberVariables.Count == 0:
            {
                // Zero-field entity: pointer identity (WiredRoutinePass handles non-empty entities).
                string result = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line: $"  {result} = icmp eq ptr %me, %{youName}");
                EmitLine(sb: _functionDefinitions, line: $"  ret i1 {result}");
                break;
            }
            default:
                EmitLine(sb: _functionDefinitions, line: "  ret i1 false");
                break;
        }

        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized $cmp routine on tuples.
    /// Lexicographic comparison: compare element-by-element, return first non-SAME result.
    /// </summary>
    private void EmitSynthesizedCmp(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType is not TupleTypeInfo tuple)
        {
            return;
        }

        string tupleStructType = GetTupleTypeName(tuple: tuple);

        EmitLine(sb: _functionDefinitions,
            line: $"define i64 @{funcName}({tupleStructType} %me, {tupleStructType} %you) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        // Look up the SAME tag value (should be 0)
        long sameTag = 0;
        (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? sameCase =
            _registry.LookupChoiceCase(caseName: "SAME");
        if (sameCase != null)
        {
            sameTag = sameCase.Value.CaseInfo.ComputedValue;
        }

        if (tuple.ElementTypes.Count == 0)
        {
            EmitLine(sb: _functionDefinitions, line: $"  ret i64 {sameTag}");
            EmitLine(sb: _functionDefinitions, line: "}");
            EmitLine(sb: _functionDefinitions, line: "");
            return;
        }

        for (int i = 0; i < tuple.ElementTypes.Count; i++)
        {
            TypeInfo elemType = tuple.ElementTypes[index: i];
            string elemLlvmType = GetLlvmType(type: elemType);

            string meElem = NextTemp();
            string youElem = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {meElem} = extractvalue {tupleStructType} %me, {i}");
            EmitLine(sb: _functionDefinitions,
                line: $"  {youElem} = extractvalue {tupleStructType} %you, {i}");

            // Call element's $cmp (ensure it's declared)
            RoutineInfo? elemCmpMethod =
                _registry.LookupMethod(type: elemType, methodName: "$cmp");
            if (elemCmpMethod != null)
            {
                GenerateFunctionDeclaration(routine: elemCmpMethod);
            }

            string cmpName = elemCmpMethod != null
                ? MangleFunctionName(routine: elemCmpMethod)
                : Q(name: $"{elemType.Name}.$cmp");
            string cmpResult = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line:
                $"  {cmpResult} = call i64 @{cmpName}({elemLlvmType} {meElem}, {elemLlvmType} {youElem})");

            // Last element: just return
            if (i == tuple.ElementTypes.Count - 1)
            {
                EmitLine(sb: _functionDefinitions, line: $"  ret i64 {cmpResult}");
            }
            else
            {
                // If not SAME, return immediately; otherwise continue to next element
                string isSame = NextTemp();
                string nextLabel = $"cmp{i + 1}";
                string retLabel = $"ret{i}";
                EmitLine(sb: _functionDefinitions,
                    line: $"  {isSame} = icmp eq i64 {cmpResult}, {sameTag}");
                EmitLine(sb: _functionDefinitions,
                    line: $"  br i1 {isSame}, label %{nextLabel}, label %{retLabel}");
                EmitLine(sb: _functionDefinitions, line: "");
                EmitLine(sb: _functionDefinitions, line: $"{retLabel}:");
                EmitLine(sb: _functionDefinitions, line: $"  ret i64 {cmpResult}");
                EmitLine(sb: _functionDefinitions, line: "");
                EmitLine(sb: _functionDefinitions, line: $"{nextLabel}:");
            }
        }

        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }
    /// <summary>
    /// Emits a string literal for use in synthesized routines and returns its global name.
    /// </summary>
    private string EmitSynthesizedStringLiteral(string value)
    {
        // Delegate to EmitStringLiteral which handles UTF-32 Text constant emission
        return EmitStringLiteral(sb: _functionDefinitions, value: value);
    }
}
