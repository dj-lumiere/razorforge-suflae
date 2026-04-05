using SemanticAnalysis.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
    private void EmitWhen(StringBuilder sb, WhenStatement whenStmt)
    {
        // Evaluate the subject expression once
        string subject = EmitExpression(sb: sb, expr: whenStmt.Expression);
        TypeInfo? subjectType = GetExpressionType(expr: whenStmt.Expression);
        string endLabel = NextLabel(prefix: "when_end");

        // Generate labels for each clause
        var clauseLabels = new List<string>();
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            clauseLabels.Add(item: NextLabel(prefix: $"when_case{i}"));
        }

        // Jump to first clause
        if (clauseLabels.Count > 0)
        {
            EmitLine(sb: sb, line: $"  br label %{clauseLabels[index: 0]}");
        }
        else
        {
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }

        // Emit each clause
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            WhenClause clause = whenStmt.Clauses[index: i];
            string currentLabel = clauseLabels[index: i];
            string nextLabel = i + 1 < clauseLabels.Count
                ? clauseLabels[index: i + 1]
                : endLabel;

            EmitLine(sb: sb, line: $"{currentLabel}:");

            // Emit pattern matching code
            string bodyLabel = NextLabel(prefix: $"when_body{i}");
            EmitPatternMatch(sb: sb,
                subject: subject,
                pattern: clause.Pattern,
                matchLabel: bodyLabel,
                failLabel: nextLabel,
                subjectType: subjectType);

            // Emit body
            EmitLine(sb: sb, line: $"{bodyLabel}:");
            bool bodyTerminated = EmitStatement(sb: sb, stmt: clause.Body);
            if (!bodyTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }
        }

        // End block
        EmitLine(sb: sb, line: $"{endLabel}:");
    }

    /// <summary>
    /// Emits code for pattern matching.
    /// Branches to matchLabel if pattern matches, failLabel otherwise.
    /// </summary>
    private void EmitPatternMatch(StringBuilder sb, string subject, Pattern pattern,
        string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        switch (pattern)
        {
            case LiteralPattern lit:
                EmitLiteralPatternMatch(sb: sb,
                    subject: subject,
                    lit: lit,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case WildcardPattern:
                // Always matches - unconditional branch
                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
                break;

            case ElsePattern elseP:
                // Always matches; optionally bind value to variable
                if (elseP.VariableName != null && subjectType != null)
                {
                    string elseType = GetLLVMType(type: subjectType);
                    string elseAddr = $"%{elseP.VariableName}.addr";
                    EmitEntryAlloca(llvmName: elseAddr, llvmType: elseType);
                    EmitLine(sb: sb, line: $"  store {elseType} {subject}, ptr {elseAddr}");
                    _localVariables[key: elseP.VariableName] = subjectType;
                }

                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
                break;

            case IdentifierPattern id:
                EmitIdentifierPatternMatch(sb: sb,
                    subject: subject,
                    id: id,
                    matchLabel: matchLabel,
                    subjectType: subjectType);
                break;

            case TypePattern typePattern:
                EmitTypePatternMatch(sb: sb,
                    subject: subject,
                    typePattern: typePattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case VariantPattern variant:
                EmitVariantPatternMatch(sb: sb,
                    subject: subject,
                    variant: variant,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case GuardPattern guardPattern:
                EmitGuardPatternMatch(sb: sb,
                    subject: subject,
                    guardPattern: guardPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case NonePattern:
                EmitNonePatternMatch(sb: sb,
                    subject: subject,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case CrashablePattern crashable:
                EmitCrashablePatternMatch(sb: sb,
                    subject: subject,
                    crashable: crashable,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case ExpressionPattern exprPattern:
                // Expression pattern: evaluate condition directly
                string condition = EmitExpression(sb: sb, expr: exprPattern.Expression);
                EmitLine(sb: sb,
                    line: $"  br i1 {condition}, label %{matchLabel}, label %{failLabel}");
                break;

            case NegatedTypePattern negType:
                EmitNegatedTypePatternMatch(sb: sb,
                    subject: subject,
                    negType: negType,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case FlagsPattern flagsPattern:
                EmitFlagsPatternMatch(sb: sb,
                    subject: subject,
                    flagsPattern: flagsPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case ComparisonPattern cmpPattern:
                EmitComparisonPatternMatch(sb: sb,
                    subject: subject,
                    cmpPattern: cmpPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case DestructuringPattern destructPattern:
                EmitDestructuringPatternMatch(sb: sb,
                    subject: subject,
                    destructPattern: destructPattern,
                    matchLabel: matchLabel,
                    subjectType: subjectType);
                break;

            case TypeDestructuringPattern typeDestructPattern:
                EmitTypeDestructuringPatternMatch(sb: sb,
                    subject: subject,
                    pattern: typeDestructPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            default:
                throw new NotImplementedException(
                    message: $"Pattern type not implemented in codegen: {pattern.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits code for literal pattern matching with correct type comparison.
    /// </summary>
    private void EmitLiteralPatternMatch(StringBuilder sb, string subject, LiteralPattern lit,
        string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        string litValue = lit.Value?.ToString() ?? "0";
        string result = NextTemp();

        // Determine LLVM type and comparison from the literal's token type
        string llvmType = lit.LiteralType switch
        {
            Lexer.TokenType.S8Literal => "i8",
            Lexer.TokenType.S16Literal => "i16",
            Lexer.TokenType.S32Literal => "i32",
            Lexer.TokenType.S64Literal => "i64",
            Lexer.TokenType.S128Literal => "i128",
            Lexer.TokenType.U8Literal => "i8",
            Lexer.TokenType.U16Literal => "i16",
            Lexer.TokenType.U32Literal => "i32",
            Lexer.TokenType.U64Literal => "i64",
            Lexer.TokenType.U128Literal => "i128",
            Lexer.TokenType.F16Literal => "half",
            Lexer.TokenType.F32Literal => "float",
            Lexer.TokenType.F64Literal => "double",
            Lexer.TokenType.F128Literal => "fp128",
            Lexer.TokenType.True or Lexer.TokenType.False => "i1",
            _ => subjectType != null
                ? GetLLVMType(type: subjectType)
                : "i64"
        };

        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isText = lit.LiteralType == Lexer.TokenType.TextLiteral;

        if (isText)
        {
            // Text comparison via Text.$eq(me, other) -> Bool (i1)
            TypeInfo? textType = _registry.LookupType(name: "Text");
            RoutineInfo? textEq = textType != null
                ? _registry.LookupMethod(type: textType, methodName: "$eq")
                : null;
            string eqFuncName = textEq != null
                ? MangleFunctionName(routine: textEq)
                : "Text$_eq";
            EmitLine(sb: sb,
                line: $"  {result} = call i1 @{eqFuncName}(ptr {subject}, ptr {litValue})");
        }
        else if (isFloat)
        {
            litValue = lit.Value switch
            {
                float f => f.ToString(format: "G9"),
                double d => d.ToString(format: "G17"),
                _ => litValue
            };
            EmitLine(sb: sb, line: $"  {result} = fcmp oeq {llvmType} {subject}, {litValue}");
        }
        else
        {
            if (lit.Value is bool b)
            {
                litValue = b
                    ? "true"
                    : "false";
            }

            EmitLine(sb: sb, line: $"  {result} = icmp eq {llvmType} {subject}, {litValue}");
        }

        EmitLine(sb: sb, line: $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for identifier pattern: bind value to variable and always match.
    /// </summary>
    private void EmitIdentifierPatternMatch(StringBuilder sb, string subject, IdentifierPattern id,
        string matchLabel, TypeInfo? subjectType)
    {
        string llvmType = subjectType != null
            ? GetLLVMType(type: subjectType)
            : "i64";
        string varAddr = $"%{id.Name}.addr";

        EmitEntryAlloca(llvmName: varAddr, llvmType: llvmType);
        EmitLine(sb: sb, line: $"  store {llvmType} {subject}, ptr {varAddr}");

        if (subjectType != null)
        {
            _localVariables[key: id.Name] = subjectType;
        }

        EmitLine(sb: sb, line: $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits code for type pattern matching.
    /// </summary>
    private void EmitTypePatternMatch(StringBuilder sb, string subject, TypePattern typePattern,
        string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        // Resolve the target type
        TypeInfo? targetType = _registry.LookupType(name: typePattern.Type.Name);

        // Determine the actual target label — if we need to bind, use an extraction block
        bool needsBind = typePattern.VariableName != null && targetType != null;
        string branchTarget = needsBind
            ? NextLabel(prefix: "type_bind")
            : matchLabel;

        // Error-handling carriers: Maybe checks i1 tag, Result/Lookup compare i64 type_id
        if (subjectType is ErrorHandlingTypeInfo errorInfo)
        {
            string carrierType = GetCarrierLLVMType(errorType: errorInfo);
            string tagType = GetCarrierTagType(kind: errorInfo.Kind);
            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb: sb,
                line: $"  {tagPtr} = getelementptr {carrierType}, ptr {subject}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  {tag} = load {tagType}, ptr {tagPtr}");

            string expectedTag = errorInfo.Kind == ErrorHandlingKind.Maybe
                ? "1"
                : (targetType != null
                    ? ComputeTypeId(fullName: targetType.FullName).ToString()
                    : "0");
            string cmp = NextTemp();
            EmitLine(sb: sb, line: $"  {cmp} = icmp eq {tagType} {tag}, {expectedTag}");
            EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{branchTarget}, label %{failLabel}");

            if (needsBind && targetType != null)
            {
                EmitLine(sb: sb, line: $"{branchTarget}:");
                string varAddr = $"%{typePattern.VariableName}.addr";

                if (errorInfo.Kind == ErrorHandlingKind.Maybe)
                {
                    string valPtr = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {valPtr} = getelementptr {carrierType}, ptr {subject}, i32 0, i32 1");
                    if (targetType is EntityTypeInfo or WrapperTypeInfo)
                    {
                        string ptrVal = NextTemp();
                        EmitLine(sb: sb, line: $"  {ptrVal} = load ptr, ptr {valPtr}");
                        EmitEntryAlloca(llvmName: varAddr, llvmType: "ptr");
                        EmitLine(sb: sb, line: $"  store ptr {ptrVal}, ptr {varAddr}");
                    }
                    else
                    {
                        string valLlvm = GetLLVMType(type: targetType);
                        string val = NextTemp();
                        EmitLine(sb: sb, line: $"  {val} = load {valLlvm}, ptr {valPtr}");
                        EmitEntryAlloca(llvmName: varAddr, llvmType: valLlvm);
                        EmitLine(sb: sb, line: $"  store {valLlvm} {val}, ptr {varAddr}");
                    }
                }
                else
                {
                    // Result/Lookup: field 1 is i64 address → inttoptr
                    string addrPtr = NextTemp();
                    string addrVal = NextTemp();
                    string handleVal = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {addrPtr} = getelementptr {carrierType}, ptr {subject}, i32 0, i32 1");
                    EmitLine(sb: sb, line: $"  {addrVal} = load i64, ptr {addrPtr}");
                    EmitLine(sb: sb, line: $"  {handleVal} = inttoptr i64 {addrVal} to ptr");
                    EmitEntryAlloca(llvmName: varAddr, llvmType: "ptr");
                    EmitLine(sb: sb, line: $"  store ptr {handleVal}, ptr {varAddr}");
                }

                _localVariables[key: typePattern.VariableName!] = targetType;
                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
            }

            return;
        }

        if (subjectType is VariantTypeInfo variant && targetType != null)
        {
            // For variants, check if any member matches the target type
            VariantMemberInfo? matchedMember = null;

            // Check for None state
            if (targetType.Name == "None")
            {
                matchedMember = variant.Members.FirstOrDefault(predicate: m => m.IsNone);
            }
            else
            {
                matchedMember = variant.FindMember(type: targetType);
            }

            if (matchedMember != null)
            {
                string tagPtr = NextTemp();
                string tag = NextTemp();
                string variantTypeName = GetVariantTypeName(variant: variant);
                EmitLine(sb: sb,
                    line:
                    $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
                EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");
                string cmp = NextTemp();
                EmitLine(sb: sb, line: $"  {cmp} = icmp eq i64 {tag}, {matchedMember.TagValue}");
                EmitLine(sb: sb,
                    line: $"  br i1 {cmp}, label %{branchTarget}, label %{failLabel}");
            }
            else
            {
                EmitLine(sb: sb, line: $"  br label %{failLabel}");
            }
        }
        else
        {
            // For entities, compare vtable pointer or type tag
            if (subjectType != null && targetType != null && subjectType.Name == targetType.Name)
            {
                EmitLine(sb: sb, line: $"  br label %{branchTarget}");
            }
            else if (subjectType is EntityTypeInfo && targetType is EntityTypeInfo &&
                     subjectType.Name != targetType.Name)
            {
                // Known incompatible entity types — cannot match
                EmitLine(sb: sb, line: $"  br label %{failLabel}");
            }
            else
            {
                // Cannot determine at compile time — fall through to match (optimistic)
                EmitLine(sb: sb, line: $"  br label %{branchTarget}");
            }
        }

        // Bind to variable if specified — emit alloca+store in a dedicated block
        if (needsBind)
        {
            EmitLine(sb: sb, line: $"{branchTarget}:");
            string bindType = GetLLVMType(type: targetType!);
            string varAddr = $"%{typePattern.VariableName}.addr";
            EmitEntryAlloca(llvmName: varAddr, llvmType: bindType);
            EmitLine(sb: sb, line: $"  store {bindType} {subject}, ptr {varAddr}");
            _localVariables[key: typePattern.VariableName!] = targetType!;
            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
        }
    }

    /// <summary>
    /// Emits code for crashable pattern matching (error case of Result/Lookup/Maybe).
    /// </summary>
    private void EmitCrashablePatternMatch(StringBuilder sb, string subject,
        CrashablePattern crashable, string matchLabel, string failLabel,
        TypeInfo? subjectType)
    {
        if (subjectType is ErrorHandlingTypeInfo errorInfo)
        {
            // Maybe has no error case — CrashablePattern cannot match
            if (errorInfo.Kind == ErrorHandlingKind.Maybe)
            {
                EmitLine(sb: sb, line: $"  br label %{failLabel}");
                return;
            }

            // Result/Lookup carrier layout: { i64 (type_id), i64 (address) }
            // type_id == 0 → ABSENT (Blank), ComputeTypeId(T) → VALID, ComputeTypeId(Error) → ERROR
            // CrashablePattern matches the ERROR case: tag != 0 && tag != ComputeTypeId(valueType)
            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb: sb,
                line: $"  {tagPtr} = getelementptr {{ i64, i64 }}, ptr {subject}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

            // tag != 0 (not absent) && tag != ComputeTypeId(T) (not valid) → error
            ulong validId = ComputeTypeId(fullName: errorInfo.ValueType.FullName);
            string notAbsent = NextTemp();
            string notValid = NextTemp();
            string cmp = NextTemp();
            EmitLine(sb: sb, line: $"  {notAbsent} = icmp ne i64 {tag}, 0");
            EmitLine(sb: sb, line: $"  {notValid} = icmp ne i64 {tag}, {validId}");
            EmitLine(sb: sb, line: $"  {cmp} = and i1 {notAbsent}, {notValid}");

            // Bind error value to variable if specified
            if (crashable.VariableName != null)
            {
                string extractLabel = NextLabel(prefix: "crash_extract");
                EmitLine(sb: sb,
                    line: $"  br i1 {cmp}, label %{extractLabel}, label %{failLabel}");

                EmitLine(sb: sb, line: $"{extractLabel}:");
                // Extract address from field 1 (i64) and convert to ptr
                string addrFieldPtr = NextTemp();
                string addrVal = NextTemp();
                string handleVal = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {addrFieldPtr} = getelementptr {{ i64, i64 }}, ptr {subject}, i32 0, i32 1");
                EmitLine(sb: sb, line: $"  {addrVal} = load i64, ptr {addrFieldPtr}");
                EmitLine(sb: sb, line: $"  {handleVal} = inttoptr i64 {addrVal} to ptr");

                string varAddr = $"%{crashable.VariableName}.addr";
                EmitEntryAlloca(llvmName: varAddr, llvmType: "ptr");
                EmitLine(sb: sb, line: $"  store ptr {handleVal}, ptr {varAddr}");

                TypeInfo errorType = errorInfo.ErrorType ?? errorInfo;
                _localVariables[key: crashable.VariableName] = errorType;
                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
            }
            else
            {
                EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
            }
        }
        else
        {
            // Not an error handling type — cannot match crashable pattern
            EmitLine(sb: sb, line: $"  br label %{failLabel}");
        }
    }

    /// <summary>
    /// Emits code for None pattern matching.
    /// For error handling types, checks DataState tag == 0 (ABSENT).
    /// For other types, falls back to pointer null check.
    /// </summary>
    private void EmitNonePatternMatch(StringBuilder sb, string subject, string matchLabel,
        string failLabel, TypeInfo? subjectType)
    {
        if (subjectType is ErrorHandlingTypeInfo errorInfo)
        {
            // Result has no None case
            if (errorInfo.Kind == ErrorHandlingKind.Result)
            {
                EmitLine(sb: sb, line: $"  br label %{failLabel}");
                return;
            }

            // Maybe: { i1, T } tag is i1; Lookup: { i64, i64 } tag is i64 — both absent when tag == 0
            string carrierType = GetCarrierLLVMType(errorType: errorInfo);
            string tagType = GetCarrierTagType(kind: errorInfo.Kind);
            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb: sb,
                line: $"  {tagPtr} = getelementptr {carrierType}, ptr {subject}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  {tag} = load {tagType}, ptr {tagPtr}");
            string cmp = NextTemp();
            EmitLine(sb: sb, line: $"  {cmp} = icmp eq {tagType} {tag}, 0");
            EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
        }
        else
        {
            // Non-error-handling type: check for null pointer
            string cmp = NextTemp();
            EmitLine(sb: sb, line: $"  {cmp} = icmp eq ptr {subject}, null");
            EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
        }
    }

    /// <summary>
    /// Emits code for variant pattern matching (is MemberType payload).
    /// </summary>
    private void EmitVariantPatternMatch(StringBuilder sb, string subject, VariantPattern variant,
        string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        // Determine variant type and struct name for GEP
        var variantType = subjectType as VariantTypeInfo;
        if (variantType == null && variant.VariantType != null)
        {
            variantType = _registry.LookupType(name: variant.VariantType) as VariantTypeInfo;
        }

        string variantStructType = variantType != null
            ? GetVariantTypeName(variant: variantType)
            : "{ i64 }";

        // Extract tag from variant (first field)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {variantStructType}, ptr {subject}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

        // Look up member by case name (which is the type name)
        int expectedTag = 0;
        VariantMemberInfo? matchedMember = null;
        if (variantType != null)
        {
            matchedMember =
                variantType.Members.FirstOrDefault(predicate: m => m.Name == variant.CaseName);
            if (matchedMember != null)
            {
                expectedTag = matchedMember.TagValue;
            }
        }

        string cmp = NextTemp();
        EmitLine(sb: sb, line: $"  {cmp} = icmp eq i64 {tag}, {expectedTag}");

        // If bindings are present, extract payload in the match block
        if (variant.Bindings is { Count: > 0 } && matchedMember is { IsNone: false })
        {
            string extractLabel = NextLabel(prefix: "variant_extract");
            EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{extractLabel}, label %{failLabel}");

            EmitLine(sb: sb, line: $"{extractLabel}:");
            // Extract payload (second field of variant struct)
            string payloadPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {payloadPtr} = getelementptr {variantStructType}, ptr {subject}, i32 0, i32 1");

            // Bind the first binding to the payload
            DestructuringBinding binding = variant.Bindings[index: 0];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? "_payload";

            TypeInfo? payloadType = matchedMember.Type;

            if (payloadType != null)
            {
                string payloadLlvm = GetLLVMType(type: payloadType);
                string payloadVal = NextTemp();
                EmitLine(sb: sb, line: $"  {payloadVal} = load {payloadLlvm}, ptr {payloadPtr}");

                string bindAddr = $"%{bindName}.addr";
                EmitEntryAlloca(llvmName: bindAddr, llvmType: payloadLlvm);
                EmitLine(sb: sb, line: $"  store {payloadLlvm} {payloadVal}, ptr {bindAddr}");
                _localVariables[key: bindName] = payloadType;
            }

            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
        }
        else
        {
            EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
        }
    }

    /// <summary>
    /// Emits code for guard pattern matching (pattern if condition).
    /// </summary>
    private void EmitGuardPatternMatch(StringBuilder sb, string subject, GuardPattern guardPattern,
        string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        // First check inner pattern
        string guardCheck = NextLabel(prefix: "guard_check");
        EmitPatternMatch(sb: sb,
            subject: subject,
            pattern: guardPattern.InnerPattern,
            matchLabel: guardCheck,
            failLabel: failLabel,
            subjectType: subjectType);

        // Then check guard condition
        EmitLine(sb: sb, line: $"{guardCheck}:");
        string guardResult = EmitExpression(sb: sb, expr: guardPattern.Guard);
        EmitLine(sb: sb, line: $"  br i1 {guardResult}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for negated type pattern matching (isnot Type).
    /// Inverts the logic of TypePattern — branches to matchLabel when type does NOT match.
    /// </summary>
    private void EmitNegatedTypePatternMatch(StringBuilder sb, string subject,
        NegatedTypePattern negType, string matchLabel, string failLabel,
        TypeInfo? subjectType)
    {
        // Invert: match→fail, fail→match compared to regular TypePattern
        TypeInfo? targetType = _registry.LookupType(name: negType.Type.Name);

        if (subjectType is VariantTypeInfo variant && targetType != null)
        {
            VariantMemberInfo? matchedMember = targetType.Name == "None"
                ? variant.Members.FirstOrDefault(predicate: m => m.IsNone)
                : variant.FindMember(type: targetType);

            if (matchedMember != null)
            {
                string tagPtr = NextTemp();
                string tag = NextTemp();
                string variantTypeName = GetVariantTypeName(variant: variant);
                EmitLine(sb: sb,
                    line:
                    $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
                EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");
                string cmp = NextTemp();
                EmitLine(sb: sb, line: $"  {cmp} = icmp ne i64 {tag}, {matchedMember.TagValue}");
                EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
            }
            else
            {
                // No matching case — always matches the negation
                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
            }
        }
        else if (subjectType != null && targetType != null && subjectType.Name == targetType.Name)
        {
            // Known same type — negation always fails
            EmitLine(sb: sb, line: $"  br label %{failLabel}");
        }
        else if (subjectType is EntityTypeInfo && targetType is EntityTypeInfo &&
                 subjectType.Name != targetType.Name)
        {
            // Known different entity types — negation always matches
            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
        }
        else
        {
            // Cannot determine — fall through to match (optimistic for negation)
            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
        }
    }

    /// <summary>
    /// Emits code for flags pattern matching in when clauses.
    /// Reuses the same bitwise logic as EmitFlagsTest.
    /// </summary>
    private void EmitFlagsPatternMatch(StringBuilder sb, string subject, FlagsPattern flagsPattern,
        string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        var flagsType = subjectType as FlagsTypeInfo;

        // Build the combined test mask
        ulong testMask = 0;
        foreach (string flagName in flagsPattern.FlagNames)
        {
            testMask |= ResolveFlagBit(flagName: flagName, flagsType: flagsType);
        }

        // Build excluded mask
        ulong excludedMask = 0;
        if (flagsPattern.ExcludedFlags != null)
        {
            foreach (string flagName in flagsPattern.ExcludedFlags)
            {
                excludedMask |= ResolveFlagBit(flagName: flagName, flagsType: flagsType);
            }
        }

        string maskStr = testMask.ToString();
        string result;

        if (flagsPattern.IsExact)
        {
            // isonly: x == mask
            result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = icmp eq i64 {subject}, {maskStr}");
        }
        else
        {
            // is: check flags based on connective
            string andResult = NextTemp();
            EmitLine(sb: sb, line: $"  {andResult} = and i64 {subject}, {maskStr}");

            if (flagsPattern.Connective == FlagsTestConnective.Or)
            {
                result = NextTemp();
                EmitLine(sb: sb, line: $"  {result} = icmp ne i64 {andResult}, 0");
            }
            else
            {
                result = NextTemp();
                EmitLine(sb: sb, line: $"  {result} = icmp eq i64 {andResult}, {maskStr}");
            }

            // Handle 'but' exclusion
            if (excludedMask > 0)
            {
                string exclAnd = NextTemp();
                EmitLine(sb: sb, line: $"  {exclAnd} = and i64 {subject}, {excludedMask}");
                string exclCmp = NextTemp();
                EmitLine(sb: sb, line: $"  {exclCmp} = icmp eq i64 {exclAnd}, 0");
                string combined = NextTemp();
                EmitLine(sb: sb, line: $"  {combined} = and i1 {result}, {exclCmp}");
                result = combined;
            }
        }

        EmitLine(sb: sb, line: $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for comparison pattern matching (== value, != value, &lt; value, etc.).
    /// </summary>
    private void EmitComparisonPatternMatch(StringBuilder sb, string subject,
        ComparisonPattern cmpPattern, string matchLabel, string failLabel,
        TypeInfo? subjectType)
    {
        string rhs = EmitExpression(sb: sb, expr: cmpPattern.Value);
        string llvmType = subjectType != null
            ? GetLLVMType(type: subjectType)
            : "i64";
        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isPtr = llvmType == "ptr";

        string result = NextTemp();

        if (cmpPattern.Operator == Lexer.TokenType.ReferenceEqual)
        {
            EmitLine(sb: sb, line: $"  {result} = icmp eq ptr {subject}, {rhs}");
        }
        else if (cmpPattern.Operator == Lexer.TokenType.ReferenceNotEqual)
        {
            EmitLine(sb: sb, line: $"  {result} = icmp ne ptr {subject}, {rhs}");
        }
        else if (isFloat)
        {
            string fcmpOp = cmpPattern.Operator switch
            {
                Lexer.TokenType.Equal => "oeq",
                Lexer.TokenType.NotEqual => "one",
                Lexer.TokenType.Less => "olt",
                Lexer.TokenType.LessEqual => "ole",
                Lexer.TokenType.Greater => "ogt",
                Lexer.TokenType.GreaterEqual => "oge",
                _ => "oeq"
            };
            EmitLine(sb: sb, line: $"  {result} = fcmp {fcmpOp} {llvmType} {subject}, {rhs}");
        }
        else if (isPtr)
        {
            string icmpOp = cmpPattern.Operator switch
            {
                Lexer.TokenType.Equal => "eq",
                Lexer.TokenType.NotEqual => "ne",
                _ => "eq"
            };
            EmitLine(sb: sb, line: $"  {result} = icmp {icmpOp} ptr {subject}, {rhs}");
        }
        else
        {
            string icmpOp = cmpPattern.Operator switch
            {
                Lexer.TokenType.Equal => "eq",
                Lexer.TokenType.NotEqual => "ne",
                Lexer.TokenType.Less => "slt",
                Lexer.TokenType.LessEqual => "sle",
                Lexer.TokenType.Greater => "sgt",
                Lexer.TokenType.GreaterEqual => "sge",
                _ => "eq"
            };
            EmitLine(sb: sb, line: $"  {result} = icmp {icmpOp} {llvmType} {subject}, {rhs}");
        }

        EmitLine(sb: sb, line: $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for destructuring pattern: extract fields and bind to variables.
    /// Always matches (destructuring is structural, not conditional).
    /// </summary>
    private void EmitDestructuringPatternMatch(StringBuilder sb, string subject,
        DestructuringPattern destructPattern, string matchLabel, TypeInfo? subjectType)
    {
        EmitDestructuringBindings(sb: sb,
            subject: subject,
            bindings: destructPattern.Bindings,
            subjectType: subjectType);
        EmitLine(sb: sb, line: $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits code for type + destructuring pattern: type check then extract fields.
    /// </summary>
    private void EmitTypeDestructuringPatternMatch(StringBuilder sb, string subject,
        TypeDestructuringPattern pattern, string matchLabel, string failLabel,
        TypeInfo? subjectType)
    {
        TypeInfo? targetType = _registry.LookupType(name: pattern.Type.Name);

        // Type check first (same logic as TypePattern)
        string extractLabel = NextLabel(prefix: "type_destruct");

        if (subjectType is VariantTypeInfo variant && targetType != null)
        {
            VariantMemberInfo? matchedMember = targetType.Name == "None"
                ? variant.Members.FirstOrDefault(predicate: m => m.IsNone)
                : variant.FindMember(type: targetType);

            if (matchedMember != null)
            {
                string tagPtr = NextTemp();
                string tag = NextTemp();
                string variantTypeName = GetVariantTypeName(variant: variant);
                EmitLine(sb: sb,
                    line:
                    $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
                EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");
                string cmp = NextTemp();
                EmitLine(sb: sb, line: $"  {cmp} = icmp eq i64 {tag}, {matchedMember.TagValue}");
                EmitLine(sb: sb,
                    line: $"  br i1 {cmp}, label %{extractLabel}, label %{failLabel}");
            }
            else
            {
                EmitLine(sb: sb, line: $"  br label %{failLabel}");
                EmitLine(sb: sb, line: $"{extractLabel}:");
                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
                return;
            }
        }
        else if (subjectType != null && targetType != null && subjectType.Name == targetType.Name)
        {
            EmitLine(sb: sb, line: $"  br label %{extractLabel}");
        }
        else if (subjectType is EntityTypeInfo && targetType is EntityTypeInfo &&
                 subjectType.Name != targetType.Name)
        {
            EmitLine(sb: sb, line: $"  br label %{failLabel}");
            EmitLine(sb: sb, line: $"{extractLabel}:");
            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
            return;
        }
        else
        {
            EmitLine(sb: sb, line: $"  br label %{extractLabel}");
        }

        // Extract and bind fields
        EmitLine(sb: sb, line: $"{extractLabel}:");
        TypeInfo? bindType = targetType ?? subjectType;
        EmitDestructuringBindings(sb: sb,
            subject: subject,
            bindings: pattern.Bindings,
            subjectType: bindType);
        EmitLine(sb: sb, line: $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits field extraction for destructuring bindings.
    /// Supports both positional and named bindings on records, entities, and tuples.
    /// </summary>
    private void EmitDestructuringBindings(StringBuilder sb, string subject,
        List<DestructuringBinding> bindings, TypeInfo? subjectType)
    {
        // Get the member variables from the subject type
        IReadOnlyList<MemberVariableInfo>? memberVariables = subjectType switch
        {
            RecordTypeInfo record => record.MemberVariables,
            EntityTypeInfo entity => entity.MemberVariables,
            TupleTypeInfo tuple => tuple.MemberVariables,
            _ => null
        };

        string structTypeName = subjectType switch
        {
            RecordTypeInfo record => record.HasDirectBackendType
                ? record.LlvmType
                :
                record.IsSingleMemberVariableWrapper
                    ?
                    GetLLVMType(type: record.UnderlyingIntrinsic!)
                    : GetRecordTypeName(record: record),
            EntityTypeInfo entity => GetEntityTypeName(entity: entity),
            TupleTypeInfo tuple =>
                $"{{ {string.Join(separator: ", ", values: tuple.ElementTypes.Select(selector: e => GetLLVMType(type: e)))} }}",
            _ => "{ }"
        };

        for (int i = 0; i < bindings.Count; i++)
        {
            DestructuringBinding binding = bindings[index: i];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_destruct{i}";

            if (bindName == "_")
            {
                continue; // Wildcard, skip
            }

            // Find the member variable by name or position
            int memberIdx = -1;
            MemberVariableInfo? memberVar = null;

            if (memberVariables != null)
            {
                if (binding.MemberVariableName != null)
                {
                    for (int j = 0; j < memberVariables.Count; j++)
                    {
                        if (memberVariables[index: j].Name == binding.MemberVariableName)
                        {
                            memberIdx = j;
                            memberVar = memberVariables[index: j];
                            break;
                        }
                    }
                }

                if (memberIdx < 0 && i < memberVariables.Count)
                {
                    memberIdx = i;
                    memberVar = memberVariables[index: i];
                }
            }

            if (memberIdx < 0 || memberVar == null)
            {
                continue;
            }

            string memberLlvmType = GetLLVMType(type: memberVar.Type);
            string memberPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {memberPtr} = getelementptr {structTypeName}, ptr {subject}, i32 0, i32 {memberIdx}");
            string memberVal = NextTemp();
            EmitLine(sb: sb, line: $"  {memberVal} = load {memberLlvmType}, ptr {memberPtr}");

            string varAddr = $"%{bindName}.addr";
            EmitEntryAlloca(llvmName: varAddr, llvmType: memberLlvmType);
            EmitLine(sb: sb, line: $"  store {memberLlvmType} {memberVal}, ptr {varAddr}");
            _localVariables[key: bindName] = memberVar.Type;
        }
    }
}
