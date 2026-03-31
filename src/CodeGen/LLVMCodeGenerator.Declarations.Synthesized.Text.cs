namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;

/// <summary>
/// Declaration code generation for synthesized text conversion and hashing routines.
/// </summary>
public partial class LLVMCodeGenerator
{
    private void EmitSynthesizedEq(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(type: routine.Parameters[index: 0].Type)
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
            case RecordTypeInfo { IsSingleMemberVariableWrapper: true } record:
            {
                // Single-member-variable wrapper: compare the underlying value
                string llvmType = GetLLVMType(type: record.UnderlyingIntrinsic!);
                string result = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line: $"  {result} = icmp eq {llvmType} %me, %{youName}");
                EmitLine(sb: _functionDefinitions, line: $"  ret i1 {result}");
                break;
            }
            case RecordTypeInfo record:
            {
                // Multi-field record: extractvalue each field, compare, AND chain
                if (record.MemberVariables.Count == 0)
                {
                    EmitLine(sb: _functionDefinitions, line: "  ret i1 true");
                    break;
                }

                string typeName = GetRecordTypeName(record: record);
                string accum = "true";
                for (int i = 0; i < record.MemberVariables.Count; i++)
                {
                    MemberVariableInfo mv = record.MemberVariables[index: i];
                    string fieldType = GetLLVMType(type: mv.Type);
                    string meField = NextTemp();
                    string youField = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {meField} = extractvalue {typeName} %me, {i}");
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {youField} = extractvalue {typeName} %{youName}, {i}");

                    string cmpResult = EmitFieldEquality(fieldType: mv.Type,
                        llvmType: fieldType,
                        meField: meField,
                        youField: youField);

                    if (accum == "true")
                    {
                        accum = cmpResult;
                    }
                    else
                    {
                        string andResult = NextTemp();
                        EmitLine(sb: _functionDefinitions,
                            line: $"  {andResult} = and i1 {accum}, {cmpResult}");
                        accum = andResult;
                    }
                }

                EmitLine(sb: _functionDefinitions, line: $"  ret i1 {accum}");
                break;
            }
            case EntityTypeInfo entity:
            {
                // Entity: GEP + load each field from both pointers, compare
                if (entity.MemberVariables.Count == 0)
                {
                    // No fields — pointer equality
                    string result = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {result} = icmp eq ptr %me, %{youName}");
                    EmitLine(sb: _functionDefinitions, line: $"  ret i1 {result}");
                    break;
                }

                string typeName = GetEntityTypeName(entity: entity);
                string accum = "true";
                for (int i = 0; i < entity.MemberVariables.Count; i++)
                {
                    MemberVariableInfo mv = entity.MemberVariables[index: i];
                    string fieldType = GetLLVMType(type: mv.Type);
                    string meFp = NextTemp();
                    string youFp = NextTemp();
                    string meField = NextTemp();
                    string youField = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {meFp} = getelementptr {typeName}, ptr %me, i32 0, i32 {i}");
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {meField} = load {fieldType}, ptr {meFp}");
                    EmitLine(sb: _functionDefinitions,
                        line:
                        $"  {youFp} = getelementptr {typeName}, ptr %{youName}, i32 0, i32 {i}");
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {youField} = load {fieldType}, ptr {youFp}");

                    string cmpResult = EmitFieldEquality(fieldType: mv.Type,
                        llvmType: fieldType,
                        meField: meField,
                        youField: youField);

                    if (accum == "true")
                    {
                        accum = cmpResult;
                    }
                    else
                    {
                        string andResult = NextTemp();
                        EmitLine(sb: _functionDefinitions,
                            line: $"  {andResult} = and i1 {accum}, {cmpResult}");
                        accum = andResult;
                    }
                }

                EmitLine(sb: _functionDefinitions, line: $"  ret i1 {accum}");
                break;
            }
            case TupleTypeInfo tuple:
            {
                if (tuple.ElementTypes.Count == 0)
                {
                    EmitLine(sb: _functionDefinitions, line: "  ret i1 true");
                    break;
                }

                string tupleStructType = GetTupleTypeName(tuple: tuple);
                string accum = "true";
                for (int i = 0; i < tuple.ElementTypes.Count; i++)
                {
                    TypeInfo elemType = tuple.ElementTypes[index: i];
                    string elemLlvmType = GetLLVMType(type: elemType);
                    string meElem = NextTemp();
                    string youElem = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {meElem} = extractvalue {tupleStructType} %me, {i}");
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {youElem} = extractvalue {tupleStructType} %{youName}, {i}");

                    string cmpResult = EmitFieldEquality(fieldType: elemType,
                        llvmType: elemLlvmType,
                        meField: meElem,
                        youField: youElem);

                    if (accum == "true")
                    {
                        accum = cmpResult;
                    }
                    else
                    {
                        string andResult = NextTemp();
                        EmitLine(sb: _functionDefinitions,
                            line: $"  {andResult} = and i1 {accum}, {cmpResult}");
                        accum = andResult;
                    }
                }

                EmitLine(sb: _functionDefinitions, line: $"  ret i1 {accum}");
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
    /// Emits a field-level equality comparison.
    /// For primitive/backend types: icmp eq. For complex types: call $eq.
    /// </summary>
    private string EmitFieldEquality(TypeInfo fieldType, string llvmType, string meField,
        string youField)
    {
        // Primitive/intrinsic types and backend-annotated records use icmp/fcmp
        if (fieldType is IntrinsicTypeInfo ||
            fieldType is RecordTypeInfo { HasDirectBackendType: true } ||
            fieldType is RecordTypeInfo { IsSingleMemberVariableWrapper: true } ||
            fieldType is ChoiceTypeInfo)
        {
            string cmpResult = NextTemp();
            if (llvmType is "float" or "double" or "half" or "fp128")
            {
                EmitLine(sb: _functionDefinitions,
                    line: $"  {cmpResult} = fcmp oeq {llvmType} {meField}, {youField}");
            }
            else
            {
                EmitLine(sb: _functionDefinitions,
                    line: $"  {cmpResult} = icmp eq {llvmType} {meField}, {youField}");
            }

            return cmpResult;
        }

        // Entity types: pointer equality
        if (fieldType is EntityTypeInfo)
        {
            string cmpResult = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {cmpResult} = icmp eq ptr {meField}, {youField}");
            return cmpResult;
        }

        // Complex types: call their $eq method
        string eqName = Q(name: $"{fieldType.Name}.$eq");
        string result = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {result} = call i1 @{eqName}({llvmType} {meField}, {llvmType} {youField})");
        return result;
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
            string elemLlvmType = GetLLVMType(type: elemType);

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
    /// Emits the body for a synthesized $represent() or $diagnose() routine.
    /// $represent: "TypeName(field: val, ...)" — bare name, open+posted fields only.
    /// $diagnose: "Module/Path.TypeName([secret] field: val, ...)" — fully qualified name, all fields.
    /// </summary>
    private void EmitSynthesizedText(RoutineInfo routine, string funcName, bool includeSecret)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        // Ensure rf_text_concat is declared (used for field concatenation)
        if (_declaredNativeFunctions.Add(item: "rf_text_concat"))
        {
            EmitLine(sb: _functionDeclarations, line: "declare ptr @rf_text_concat(ptr, ptr)");
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        int savedTempCounter = _tempCounter;

        // $represent uses bare type name, $diagnose uses fully qualified name
        string typeName = includeSecret
            ? routine.OwnerType.FullName
            : routine.OwnerType.Name;

        // Get fields to include
        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        // Tuples: emit "(item0, item1, ...)" for represent, "ValueTuple/Tuple[T1, T2, ...](item0, item1, ...)" for diagnose
        if (routine.OwnerType is TupleTypeInfo tuple)
        {
            string tupleStructType = GetTupleTypeName(tuple: tuple);

            // Determine ValueTuple vs Tuple: any entity element → Tuple, all value types → ValueTuple
            bool isValueTuple = tuple.ElementTypes.All(predicate: t => t is not EntityTypeInfo);
            string tuplePrefix = isValueTuple
                ? "ValueTuple"
                : "Tuple";
            string tupleDisplayName =
                $"{tuplePrefix}[{string.Join(separator: ", ", values: tuple.ElementTypes.Select(selector: t => t.Name))}]";

            // Start with "(" for represent, "ValueTuple/Tuple[T1, T2, ...](" for diagnose
            string openLiteral = includeSecret
                ? $"{tupleDisplayName}("
                : "(";
            string tupleCur = EmitSynthesizedStringLiteral(value: openLiteral);

            for (int i = 0; i < tuple.ElementTypes.Count; i++)
            {
                TypeInfo elemTypeInfo = tuple.ElementTypes[index: i];
                string elemLlvmType = GetLLVMType(type: elemTypeInfo);

                // Add ", " separator after first element
                if (i > 0)
                {
                    string sep = EmitSynthesizedStringLiteral(value: ", ");
                    string withSep = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line:
                        $"  {withSep} = call ptr @rf_text_concat(ptr {tupleCur}, ptr {sep})");
                    tupleCur = withSep;
                }

                // Extract element value
                string elemVal = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line: $"  {elemVal} = extractvalue {tupleStructType} %me, {i}");

                // Always use $represent for inner values (diagnose shows envelope, represent for contents)
                string elemText = EmitSynthesizedValueToText(fieldType: elemTypeInfo,
                    llvmType: elemLlvmType,
                    value: elemVal,
                    useDiagnose: false);

                // Concat
                string withElem = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line:
                    $"  {withElem} = call ptr @rf_text_concat(ptr {tupleCur}, ptr {elemText})");
                tupleCur = withElem;
            }

            // Append ")"
            string closeParen = EmitSynthesizedStringLiteral(value: ")");
            string tupleResult = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line:
                $"  {tupleResult} = call ptr @rf_text_concat(ptr {tupleCur}, ptr {closeParen})");
            EmitLine(sb: _functionDefinitions, line: $"  ret ptr {tupleResult}");
            EmitLine(sb: _functionDefinitions, line: "}");
            EmitLine(sb: _functionDefinitions, line: "");
            return;
        }

        // Choice: $represent → "CASE_NAME", $diagnose → "TypeName(CASE_NAME, value: N)"
        if (routine.OwnerType is ChoiceTypeInfo choiceOwner)
        {
            EmitChoiceRepresent(choice: choiceOwner, includeSecret: includeSecret);
            EmitLine(sb: _functionDefinitions, line: "}");
            EmitLine(sb: _functionDefinitions, line: "");
            return;
        }

        // Flags: $represent → "FLAG1 and FLAG2", $diagnose → "TypeName(FLAG1 and FLAG2, value: N)"
        if (routine.OwnerType is FlagsTypeInfo flagsOwner)
        {
            EmitFlagsRepresent(flags: flagsOwner, includeSecret: includeSecret);
            EmitLine(sb: _functionDefinitions, line: "}");
            EmitLine(sb: _functionDefinitions, line: "");
            return;
        }

        if (fields == null)
        {
            string nameStr = EmitSynthesizedStringLiteral(value: typeName);
            EmitLine(sb: _functionDefinitions, line: $"  ret ptr {nameStr}");
            EmitLine(sb: _functionDefinitions, line: "}");
            EmitLine(sb: _functionDefinitions, line: "");
            return;
        }

        // Filter out secret fields for $represent (but include for $diagnose)
        var visibleFields = new List<(MemberVariableInfo MV, int Index)>();
        for (int i = 0; i < fields.Count; i++)
        {
            if (includeSecret || fields[index: i].Visibility != VisibilityModifier.Secret)
            {
                visibleFields.Add(item: (fields[index: i], i));
            }
        }

        // Build "TypeName(field: val, ...)"
        string current;
        {
            string prefix = $"{typeName}(";
            current = EmitSynthesizedStringLiteral(value: prefix);
        }

        for (int vi = 0; vi < visibleFields.Count; vi++)
        {
            (MemberVariableInfo mv, int fieldIdx) = visibleFields[index: vi];
            string fieldType = GetLLVMType(type: mv.Type);

            // Add "[secret] fieldName: " or "fieldName: " prefix
            string secretTag = includeSecret && mv.Visibility == VisibilityModifier.Secret
                ? "[secret] "
                : "";
            string fieldPrefix = vi > 0
                ? $", {secretTag}{mv.Name}: "
                : $"{secretTag}{mv.Name}: ";
            string fieldPrefixStr = EmitSynthesizedStringLiteral(value: fieldPrefix);

            // Concat field prefix
            string withPrefix = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line:
                $"  {withPrefix} = call ptr @rf_text_concat(ptr {current}, ptr {fieldPrefixStr})");
            current = withPrefix;

            // Get field value
            string fieldValue;
            switch (routine.OwnerType)
            {
                case RecordTypeInfo { HasDirectBackendType: true }:
                case RecordTypeInfo { IsSingleMemberVariableWrapper: true }:
                    fieldValue = "%me";
                    break;
                case RecordTypeInfo rec:
                {
                    string recTypeName = GetRecordTypeName(record: rec);
                    fieldValue = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {fieldValue} = extractvalue {recTypeName} %me, {fieldIdx}");
                    break;
                }
                case EntityTypeInfo ent:
                {
                    string entTypeName = GetEntityTypeName(entity: ent);
                    string fp = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line:
                        $"  {fp} = getelementptr {entTypeName}, ptr %me, i32 0, i32 {fieldIdx}");
                    fieldValue = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {fieldValue} = load {fieldType}, ptr {fp}");
                    break;
                }
                default:
                    fieldValue = "%me";
                    break;
            }

            // Convert field value to Text (use $diagnose for diagnose mode)
            string fieldText = EmitSynthesizedValueToText(fieldType: mv.Type,
                llvmType: fieldType,
                value: fieldValue,
                useDiagnose: includeSecret);

            // Concat field text
            string withField = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {withField} = call ptr @rf_text_concat(ptr {current}, ptr {fieldText})");
            current = withField;
        }

        // Append closing ")"
        string suffix = EmitSynthesizedStringLiteral(value: ")");
        string result = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {result} = call ptr @rf_text_concat(ptr {current}, ptr {suffix})");

        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {result}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits code to convert a value to Text for synthesized $represent()/$diagnose() routines.
    /// When useDiagnose is false, calls Text.$create(from: T) which delegates to T.$represent().
    /// When useDiagnose is true, calls T.$diagnose() directly.
    /// </summary>
    private string EmitSynthesizedValueToText(TypeInfo fieldType, string llvmType, string value,
        bool useDiagnose = false)
    {
        // Text fields need no conversion
        if (fieldType.Name == "Text")
        {
            return value;
        }

        if (useDiagnose)
        {
            // Call T.$diagnose() directly
            RoutineInfo? diagRoutine =
                _registry.LookupRoutine(fullName: $"{fieldType.FullName}.$diagnose");
            if (diagRoutine != null)
            {
                GenerateFunctionDeclaration(routine: diagRoutine);
                string diagName = MangleFunctionName(routine: diagRoutine);
                string diagResult = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line: $"  {diagResult} = call ptr @{diagName}({llvmType} {value})");
                return diagResult;
            }
            // Fall through to Text.$create if no $diagnose found
        }

        // Look up the Text.$create overload for this specific parameter type
        RoutineInfo? createRoutine =
            _registry.LookupRoutineOverload(baseName: "Text.$create", argTypes: [fieldType]) ??
            _registry.LookupRoutine(fullName: "Text.$create");
        if (createRoutine != null)
        {
            GenerateFunctionDeclaration(routine: createRoutine);
        }

        string createName = createRoutine != null
            ? MangleFunctionName(routine: createRoutine)
            : "Text.$create";
        string textResult = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {textResult} = call ptr @{createName}({llvmType} {value})");
        return textResult;
    }

    /// <summary>
    /// Emits choice $represent/$diagnose body.
    /// $represent: switch on value, return case name string (e.g., "READ").
    /// $diagnose: "TypeName(CASE_NAME, value: N)".
    /// </summary>
    private void EmitChoiceRepresent(ChoiceTypeInfo choice, bool includeSecret)
    {
        StringBuilder sb = _functionDefinitions;
        // $represent uses bare type name, $diagnose uses fully qualified name
        string typeName = includeSecret
            ? choice.FullName
            : choice.Name;

        if (choice.Cases.Count == 0)
        {
            string fallback = includeSecret
                ? EmitSynthesizedStringLiteral(value: $"{typeName}(<unknown>, value: 0)")
                : EmitSynthesizedStringLiteral(value: "<unknown>");
            EmitLine(sb: sb, line: $"  ret ptr {fallback}");
            return;
        }

        // Switch on %me to get case name
        string defaultLabel = "sw.default";
        EmitLine(sb: sb, line: $"  switch i32 %me, label %{defaultLabel} [");
        for (int i = 0; i < choice.Cases.Count; i++)
        {
            EmitLine(sb: sb,
                line: $"    i32 {choice.Cases[index: i].ComputedValue}, label %sw.case.{i}");
        }

        EmitLine(sb: sb, line: "  ]");

        // Emit each case block
        for (int i = 0; i < choice.Cases.Count; i++)
        {
            ChoiceCaseInfo c = choice.Cases[index: i];
            EmitLine(sb: sb, line: $"sw.case.{i}:");
            if (includeSecret)
            {
                // $diagnose: "TypeName(CASE, value: N)"
                string diagStr = EmitSynthesizedStringLiteral(
                    value: $"{typeName}({c.Name}, value: {c.ComputedValue})");
                EmitLine(sb: sb, line: $"  ret ptr {diagStr}");
            }
            else
            {
                // $represent: just "CASE"
                string caseStr = EmitSynthesizedStringLiteral(value: c.Name);
                EmitLine(sb: sb, line: $"  ret ptr {caseStr}");
            }
        }

        // Default: unknown value — convert to text
        EmitLine(sb: sb, line: $"{defaultLabel}:");
        if (includeSecret)
        {
            string prefix = EmitSynthesizedStringLiteral(value: $"{typeName}(<unknown>, value: ");
            string suffix = EmitSynthesizedStringLiteral(value: ")");
            string valText = EmitSynthesizedValueToText(
                fieldType: _registry.LookupType(name: "S32") ?? new RecordTypeInfo(name: "S32"),
                llvmType: "i32",
                value: "%me");
            string cat1 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {cat1} = call ptr @rf_text_concat(ptr {prefix}, ptr {valText})");
            string cat2 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {cat2} = call ptr @rf_text_concat(ptr {cat1}, ptr {suffix})");
            EmitLine(sb: sb, line: $"  ret ptr {cat2}");
        }
        else
        {
            string valText = EmitSynthesizedValueToText(
                fieldType: _registry.LookupType(name: "S32") ?? new RecordTypeInfo(name: "S32"),
                llvmType: "i32",
                value: "%me");
            EmitLine(sb: sb, line: $"  ret ptr {valText}");
        }
    }

    /// <summary>
    /// Emits flags $represent/$diagnose body.
    /// $represent: "FLAG1 and FLAG2" (active flags concatenated with " and ").
    /// $diagnose: "TypeName(FLAG1 and FLAG2, value: N)".
    /// </summary>
    private void EmitFlagsRepresent(FlagsTypeInfo flags, bool includeSecret)
    {
        StringBuilder sb = _functionDefinitions;
        // $represent uses bare type name, $diagnose uses fully qualified name
        string typeName = includeSecret
            ? flags.FullName
            : flags.Name;

        if (flags.Members.Count == 0)
        {
            string fallback = includeSecret
                ? EmitSynthesizedStringLiteral(value: $"{typeName}(<none>, value: 0)")
                : EmitSynthesizedStringLiteral(value: "<none>");
            EmitLine(sb: sb, line: $"  ret ptr {fallback}");
            return;
        }

        // Build the flag names string by checking each bit
        string emptyStr = EmitSynthesizedStringLiteral(value: "");
        string andSep = EmitSynthesizedStringLiteral(value: " and ");
        string current = emptyStr;
        string isFirst = "1"; // i1 tracking whether we've added any flag yet (1 = still first)

        for (int i = 0; i < flags.Members.Count; i++)
        {
            FlagsMemberInfo member = flags.Members[index: i];
            long mask = 1L << member.BitPosition;

            // Check if this bit is set: (%me & mask) != 0
            string masked = NextTemp();
            EmitLine(sb: sb, line: $"  {masked} = and i64 %me, {mask}");
            string isSet = NextTemp();
            EmitLine(sb: sb, line: $"  {isSet} = icmp ne i64 {masked}, 0");

            // Branch: if set, add this name
            string setLabel = $"flag.set.{i}";
            string skipLabel = $"flag.skip.{i}";
            string mergeLabel = $"flag.merge.{i}";
            EmitLine(sb: sb, line: $"  br i1 {isSet}, label %{setLabel}, label %{skipLabel}");

            // Set branch: concat separator + name
            EmitLine(sb: sb, line: $"{setLabel}:");
            string nameStr = EmitSynthesizedStringLiteral(value: member.Name);

            // If not first, prepend " and "
            string needsSep = NextTemp();
            EmitLine(sb: sb, line: $"  {needsSep} = icmp eq ptr {current}, {emptyStr}");
            string withSep = NextTemp();
            EmitLine(sb: sb,
                line: $"  br i1 {needsSep}, label %{setLabel}.nosep, label %{setLabel}.sep");

            EmitLine(sb: sb, line: $"{setLabel}.sep:");
            string catSep = NextTemp();
            EmitLine(sb: sb,
                line: $"  {catSep} = call ptr @rf_text_concat(ptr {current}, ptr {andSep})");
            string catName1 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {catName1} = call ptr @rf_text_concat(ptr {catSep}, ptr {nameStr})");
            EmitLine(sb: sb, line: $"  br label %{mergeLabel}");

            EmitLine(sb: sb, line: $"{setLabel}.nosep:");
            string catName2 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {catName2} = call ptr @rf_text_concat(ptr {current}, ptr {nameStr})");
            EmitLine(sb: sb, line: $"  br label %{mergeLabel}");

            // Skip branch
            EmitLine(sb: sb, line: $"{skipLabel}:");
            EmitLine(sb: sb, line: $"  br label %{mergeLabel}");

            // Merge: phi to pick the right string
            EmitLine(sb: sb, line: $"{mergeLabel}:");
            string merged = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {merged} = phi ptr [ {catName1}, %{setLabel}.sep ], [ {catName2}, %{setLabel}.nosep ], [ {current}, %{skipLabel} ]");
            current = merged;
        }

        // Handle zero value (no flags set)
        string isZero = NextTemp();
        EmitLine(sb: sb, line: $"  {isZero} = icmp eq ptr {current}, {emptyStr}");
        string noneStr = EmitSynthesizedStringLiteral(value: "<none>");
        string finalName = NextTemp();
        EmitLine(sb: sb,
            line: $"  {finalName} = select i1 {isZero}, ptr {noneStr}, ptr {current}");

        if (includeSecret)
        {
            // $diagnose: "TypeName(FLAGS, value: N)"
            string prefix = EmitSynthesizedStringLiteral(value: $"{typeName}(");
            string midStr = EmitSynthesizedStringLiteral(value: ", value: ");
            string suffix = EmitSynthesizedStringLiteral(value: ")");
            string valText = EmitSynthesizedValueToText(
                fieldType: _registry.LookupType(name: "S64") ?? new RecordTypeInfo(name: "S64"),
                llvmType: "i64",
                value: "%me");
            string cat1 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {cat1} = call ptr @rf_text_concat(ptr {prefix}, ptr {finalName})");
            string cat2 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {cat2} = call ptr @rf_text_concat(ptr {cat1}, ptr {midStr})");
            string cat3 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {cat3} = call ptr @rf_text_concat(ptr {cat2}, ptr {valText})");
            string cat4 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {cat4} = call ptr @rf_text_concat(ptr {cat3}, ptr {suffix})");
            EmitLine(sb: sb, line: $"  ret ptr {cat4}");
        }
        else
        {
            // $represent: just the flag names
            EmitLine(sb: sb, line: $"  ret ptr {finalName}");
        }
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
