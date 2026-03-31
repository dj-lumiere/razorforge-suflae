namespace Compiler.CodeGen;

using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Declaration code generation for synthesized equality and lookup-style routines.
/// </summary>
public partial class LLVMCodeGenerator
{
    private RoutineDeclaration? FindGenericAstRoutine(string genericAstName,
        int expectedParamCount = -1, string? firstParamTypeHint = null)
    {
        // Check if we need a generic routine specifically (name ends with [generic] marker)
        bool requireGeneric = genericAstName.EndsWith(value: "[generic]");
        string baseName = requireGeneric
            ? genericAstName[..genericAstName.IndexOf(value: "[generic]")]
            : genericAstName;

        // Collect all matching candidates (when disambiguating by param type)
        RoutineDeclaration? firstMatch = null;

        bool MatchesRoutine(RoutineDeclaration routine)
        {
            if (routine.Name != baseName)
            {
                return false;
            }

            if (requireGeneric && routine.GenericParameters is not { Count: > 0 })
            {
                return false;
            }

            if (expectedParamCount >= 0 && routine.Parameters.Count != expectedParamCount)
            {
                return false;
            }

            return true;
        }

        bool MatchesParamType(RoutineDeclaration routine)
        {
            if (firstParamTypeHint == null || routine.Parameters.Count == 0)
            {
                return true;
            }

            string astParamType = routine.Parameters[index: 0].Type.Name;
            return astParamType.StartsWith(value: firstParamTypeHint);
        }

        // Search user programs first, then stdlib
        foreach ((Program userProgram, string _, string _) in _userPrograms)
        {
            foreach (IAstNode decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine: routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine: routine))
                    {
                        return routine;
                    }
                }
            }
        }

        foreach ((Program program, string _, string _) in _stdlibPrograms)
        {
            foreach (IAstNode decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine: routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine: routine))
                    {
                        return routine;
                    }
                }
            }
        }

        // If param type hint didn't match any, fall back to first match
        if (firstMatch != null)
        {
            return firstMatch;
        }

        // If no exact parameter match found and we filtered by count, retry without filter
        if (expectedParamCount >= 0)
        {
            return FindGenericAstRoutine(genericAstName: genericAstName,
                expectedParamCount: -1,
                firstParamTypeHint: firstParamTypeHint);
        }

        return null;
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions.
    /// Handles direct substitution (T → S64), parameterized types (List[T] → List[S64]),
    /// and generic definitions with substitutable params.
    /// </summary>
    private TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        // Direct substitution: T → S64
        if (subs.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Parameterized: List[T] → List[S64] (substitute type arguments)
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo resolved = ResolveSubstitutedType(type: arg, subs: subs);
                substitutedArgs.Add(item: resolved);
                if (!ReferenceEquals(objA: resolved, objB: arg))
                {
                    anySubstituted = true;
                }
            }

            if (anySubstituted)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: substitutedArgs);
                }
            }
        }

        // Generic definition with substitutable params: List with GenericParameters ["T"]
        if (type is { IsGenericDefinition: true, GenericParameters: not null } &&
            type.TypeArguments == null)
        {
            var typeArgs = type.GenericParameters
                               .Select(selector: gp =>
                                    subs.TryGetValue(key: gp, value: out TypeInfo? s)
                                        ? s
                                        : _registry.LookupType(name: gp))
                               .Where(predicate: t => t != null)
                               .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
            {
                return _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs!);
            }
        }

        return type;
    }

    /// <summary>
    /// Builds a resolved RoutineInfo for monomorphization.
    /// Creates a new RoutineInfo with the resolved owner type and substituted parameter/return types.
    /// </summary>
    private RoutineInfo BuildResolvedRoutineInfo(MonomorphizationEntry entry)
    {
        RoutineInfo generic = entry.GenericMethod;
        Dictionary<string, TypeInfo> subs = entry.TypeSubstitutions;

        // Substitute parameter types (handles both direct T→S64 and parameterized List[T]→List[S64])
        var resolvedParams = generic.Parameters
                                    .Select(selector: p =>
                                     {
                                         TypeInfo resolvedType =
                                             ResolveSubstitutedType(type: p.Type, subs: subs);
                                         return p.WithSubstitutedType(newType: resolvedType);
                                     })
                                    .ToList();

        // Substitute return type
        TypeInfo? resolvedReturnType = generic.ReturnType;
        if (resolvedReturnType != null)
        {
            resolvedReturnType = ResolveSubstitutedType(type: resolvedReturnType, subs: subs);
        }

        return new RoutineInfo(name: generic.Name)
        {
            Kind = generic.Kind,
            OwnerType = entry.ResolvedOwnerType,
            Parameters = resolvedParams,
            ReturnType = resolvedReturnType,
            IsFailable = generic.IsFailable,
            DeclaredModification = generic.DeclaredModification,
            ModificationCategory = generic.ModificationCategory,
            Visibility = generic.Visibility,
            Location = generic.Location,
            Module = generic.Module,
            Annotations = generic.Annotations,
            CallingConvention = generic.CallingConvention,
            IsVariadic = generic.IsVariadic,
            IsDangerous = generic.IsDangerous,
            Storage = generic.Storage,
            AsyncStatus = generic.AsyncStatus
        };
    }

    /// <summary>
    /// Generates LLVM IR bodies for all synthesized routines.
    /// These routines are registered by the semantic analyzer (Phase 2.55/2.6) with IsSynthesized = true
    /// but have no AST body — their IR is emitted directly here.
    /// </summary>
    private void GenerateSynthesizedRoutines()
    {
        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            if (!routine.IsSynthesized)
            {
                continue;
            }

            // Skip generic definitions, routines with error types,
            // and routines on generic owner types (e.g., List[T].$diagnose)
            if (routine.IsGenericDefinition || HasErrorTypes(routine: routine) ||
                routine.OwnerType is { IsGenericDefinition: true })
            {
                continue;
            }

            string funcName = MangleFunctionName(routine: routine);

            // Only emit synthesized routines that were declared (actually referenced)
            if (!_generatedFunctions.Contains(item: funcName))
            {
                continue;
            }

            // Skip if already generated
            if (!_generatedFunctionDefs.Add(item: funcName))
            {
                continue;
            }

            // Mark in declarations set to prevent declare/define conflicts
            _generatedFunctions.Add(item: funcName);

            EmitSynthesizedRoutineBody(routine: routine, funcName: funcName);
        }
    }

    /// <summary>
    /// Emits the LLVM IR body for a single synthesized routine.
    /// Used by both GenerateSynthesizedRoutines (for non-generic types) and
    /// MonomorphizeGenericMethods (for monomorphized generic types).
    /// </summary>
    private void EmitSynthesizedRoutineBody(RoutineInfo routine, string funcName)
    {
        switch (routine.Name)
        {
            case "$ne":
                EmitSynthesizedNe(routine: routine, funcName: funcName);
                break;
            case "$notcontains":
                EmitSynthesizedNotContains(routine: routine, funcName: funcName);
                break;
            case "$lt":
                EmitSynthesizedCmpDerived(routine: routine,
                    funcName: funcName,
                    tagValue: -1,
                    cmpOp: "eq");
                break;
            case "$le":
                EmitSynthesizedCmpDerived(routine: routine,
                    funcName: funcName,
                    tagValue: 1,
                    cmpOp: "ne");
                break;
            case "$gt":
                EmitSynthesizedCmpDerived(routine: routine,
                    funcName: funcName,
                    tagValue: 1,
                    cmpOp: "eq");
                break;
            case "$ge":
                EmitSynthesizedCmpDerived(routine: routine,
                    funcName: funcName,
                    tagValue: -1,
                    cmpOp: "ne");
                break;
            case "$eq":
                EmitSynthesizedEq(routine: routine, funcName: funcName);
                break;
            case "$cmp":
                EmitSynthesizedCmp(routine: routine, funcName: funcName);
                break;
            case "$represent":
                EmitSynthesizedText(routine: routine, funcName: funcName, includeSecret: false);
                break;
            case "$diagnose":
                EmitSynthesizedText(routine: routine, funcName: funcName, includeSecret: true);
                break;
            case "$hash":
                EmitSynthesizedHash(routine: routine, funcName: funcName);
                break;
            case "S32":
                // TODO: this should be a creation
                EmitSynthesizedIdentityCast(routine: routine,
                    funcName: funcName,
                    llvmRetType: "i32");
                break;
            case "S64":
                // TODO: this should be a creation
                EmitSynthesizedIdentityCast(routine: routine,
                    funcName: funcName,
                    llvmRetType: "i64");
                break;
            case "U64":
                // TODO: this should be a creation
                EmitSynthesizedIdentityCast(routine: routine,
                    funcName: funcName,
                    llvmRetType: "i64");
                break;
            case "id":
                // TODO: Remove this at all
                EmitSynthesizedId(routine: routine, funcName: funcName);
                break;
            case "copy!":
                // TODO: Remove this for entity
                EmitSynthesizedCopy(routine: routine, funcName: funcName);
                break;
            case "$create":
                if (routine.OwnerType?.Name == "Data")
                {
                    EmitSynthesizedDataCreate(routine: routine, funcName: funcName);
                }
                else
                {
                    EmitSynthesizedTextCreate(routine: routine, funcName: funcName);
                }

                break;
            case "$create!":
                EmitSynthesizedChoiceCreateFromText(routine: routine, funcName: funcName);
                break;
            case "$same":
                EmitSynthesizedSame(routine: routine, funcName: funcName);
                break;
            case "$notsame":
                EmitSynthesizedNotSame(routine: routine, funcName: funcName);
                break;
            case "all_on":
                EmitSynthesizedAllOn(routine: routine, funcName: funcName);
                break;
            case "all_off":
                EmitSynthesizedAllOff(routine: routine, funcName: funcName);
                break;
            case "all_cases":
                EmitSynthesizedAllCases(routine: routine, funcName: funcName);
                break;
            // BuilderService infos
            case "type_name":
                EmitSynthesizedTypeName(routine: routine, funcName: funcName);
                break;
            case "type_kind":
                EmitSynthesizedTypeKind(routine: routine, funcName: funcName);
                break;
            case "type_id":
                EmitSynthesizedTypeId(routine: routine, funcName: funcName);
                break;
            case "module_name":
                EmitSynthesizedModuleName(routine: routine, funcName: funcName);
                break;
            case "member_variable_count":
                EmitSynthesizedFieldCount(routine: routine, funcName: funcName);
                break;
            case "is_generic":
                EmitSynthesizedIsGeneric(routine: routine, funcName: funcName);
                break;
            case "protocols":
                EmitSynthesizedProtocols(routine: routine, funcName: funcName);
                break;
            case "routine_names":
                EmitSynthesizedRoutineNames(routine: routine, funcName: funcName);
                break;
            case "annotations":
                EmitSynthesizedAnnotations(routine: routine, funcName: funcName);
                break;
            case "data_size":
                EmitSynthesizedDataSize(routine: routine, funcName: funcName);
                break;
            case "member_variable_info":
                EmitSynthesizedMemberVariableInfo(routine: routine, funcName: funcName);
                break;
            case "all_member_variables":
                EmitSynthesizedAllFields(routine: routine, funcName: funcName);
                break;
            case "open_member_variables":
                EmitSynthesizedOpenFields(routine: routine, funcName: funcName);
                break;
            // BuilderService platform/build info
            case "page_size":
                EmitSynthesizedBuilderServiceU64(routine: routine,
                    funcName: funcName,
                    value: 4096);
                break;
            case "cache_line":
                EmitSynthesizedBuilderServiceU64(routine: routine, funcName: funcName, value: 64);
                break;
            case "word_size":
                EmitSynthesizedBuilderServiceU64(routine: routine,
                    funcName: funcName,
                    value: _pointerBitWidth / 8);
                break;
            case "target_os":
                EmitSynthesizedBuilderServiceText(routine: routine,
                    funcName: funcName,
                    value: DetectTargetOS());
                break;
            case "target_arch":
                EmitSynthesizedBuilderServiceText(routine: routine,
                    funcName: funcName,
                    value: DetectTargetArch());
                break;
            case "builder_version":
                EmitSynthesizedBuilderServiceText(routine: routine,
                    funcName: funcName,
                    value: typeof(LLVMCodeGenerator).Assembly.GetName().Version?.ToString(fieldCount: 3) ?? "0.0.0");
                break;
            case "build_mode":
                // TODO: There is only four modes: DEBUG, RELEASE, RELEASE-TIME, RELEASE-SPACE
                #if DEBUG
                EmitSynthesizedBuilderServiceU64(routine: routine,
                    funcName: funcName,
                    value: 0); // BuildMode.DEBUG
                #else
                EmitSynthesizedBuilderServiceU64(routine, funcName, 1); // BuildMode.RELEASE
                #endif
                break;
            case "build_timestamp":
                EmitSynthesizedBuilderServiceText(routine: routine,
                    funcName: funcName,
                    value: DateTime.UtcNow.ToString(format: "o"));
                break;
            // New BuilderService per-type routines
            case "generic_args":
                EmitSynthesizedGenericArgs(routine: routine, funcName: funcName);
                break;
            case "full_type_name":
                EmitSynthesizedFullTypeName(routine: routine, funcName: funcName);
                break;
            case "protocol_info":
                EmitSynthesizedProtocolInfo(routine: routine, funcName: funcName);
                break;
            case "routine_info":
                EmitSynthesizedRoutineInfoList(routine: routine, funcName: funcName);
                break;
            case "dependencies":
                EmitSynthesizedDependencies(routine: routine, funcName: funcName);
                break;
            case "member_type_id":
                EmitSynthesizedMemberTypeId(routine: routine, funcName: funcName);
                break;
            case "var_name":
                // var_name() is inlined at call site, emit a fallback returning "<unknown>"
                EmitSynthesizedBuilderServiceTextNoOwner(routine: routine,
                    funcName: funcName,
                    value: "<unknown>");
                break;
        }
    }

    /// <summary>
    /// Emits the body for a synthesized $ne routine: not $eq(me, you).
    /// </summary>
    private void EmitSynthesizedNe(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        // Build parameter types for the 'you' parameter
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(type: routine.Parameters[index: 0].Type)
            : meType;
        string youName = routine.Parameters.Count > 0
            ? routine.Parameters[index: 0].Name
            : "you";

        // Look up the $eq function name on the same owner type
        string eqFuncName = Q(name: $"{routine.OwnerType.Name}.$eq");

        EmitLine(sb: _functionDefinitions,
            line: $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions,
            line: $"  %eq = call i1 @{eqFuncName}({meType} %me, {youType} %{youName})");
        EmitLine(sb: _functionDefinitions, line: "  %ne = xor i1 %eq, true");
        EmitLine(sb: _functionDefinitions, line: "  ret i1 %ne");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized $notcontains routine: not $contains(me, item).
    /// </summary>
    private void EmitSynthesizedNotContains(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        string itemType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(type: routine.Parameters[index: 0].Type)
            : meType;
        string itemName = routine.Parameters.Count > 0
            ? routine.Parameters[index: 0].Name
            : "item";

        string containsFuncName = Q(name: $"{routine.OwnerType.FullName}.$contains");

        EmitLine(sb: _functionDefinitions,
            line: $"define i1 @{funcName}({meType} %me, {itemType} %{itemName}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions,
            line:
            $"  %contains = call i1 @{containsFuncName}({meType} %me, {itemType} %{itemName})");
        EmitLine(sb: _functionDefinitions, line: "  %notcontains = xor i1 %contains, true");
        EmitLine(sb: _functionDefinitions, line: "  ret i1 %notcontains");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized $same routine: pointer identity comparison.
    /// </summary>
    private void EmitSynthesizedSame(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(type: routine.Parameters[index: 0].Type)
            : meType;

        EmitLine(sb: _functionDefinitions,
            line: $"define i1 @{funcName}({meType} %me, {youType} %you) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  %same = icmp eq {meType} %me, %you");
        EmitLine(sb: _functionDefinitions, line: "  ret i1 %same");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized $notsame routine: not $same(me, you).
    /// </summary>
    private void EmitSynthesizedNotSame(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(type: routine.Parameters[index: 0].Type)
            : meType;

        EmitLine(sb: _functionDefinitions,
            line: $"define i1 @{funcName}({meType} %me, {youType} %you) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  %same = icmp ne {meType} %me, %you");
        EmitLine(sb: _functionDefinitions, line: "  ret i1 %same");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized comparison operator derived from $cmp.
    /// E.g., $lt = $cmp(me, you) == -1 (ME_SMALL).
    /// </summary>
    private void EmitSynthesizedCmpDerived(RoutineInfo routine, string funcName, long tagValue,
        string cmpOp)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        // Look up the actual tag value from the registry if ComparisonSign is defined
        long resolvedTag = tagValue;
        string caseName = tagValue switch
        {
            -1 => "ME_SMALL",
            0 => "SAME",
            1 => "ME_LARGE",
            _ => ""
        };
        if (caseName.Length > 0)
        {
            (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? choiceCase =
                _registry.LookupChoiceCase(caseName: caseName);
            if (choiceCase != null)
            {
                resolvedTag = choiceCase.Value.CaseInfo.ComputedValue;
            }
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(type: routine.Parameters[index: 0].Type)
            : meType;
        string youName = routine.Parameters.Count > 0
            ? routine.Parameters[index: 0].Name
            : "you";

        // Ensure $cmp is declared/generated for the owner type
        RoutineInfo? cmpMethod =
            _registry.LookupMethod(type: routine.OwnerType, methodName: "$cmp");
        string cmpFuncName;
        if (cmpMethod != null)
        {
            cmpFuncName = MangleFunctionName(routine: cmpMethod);
            // For synthesized $cmp (e.g., tuples), emit the define directly
            // since GenerateSynthesizedRoutines may have already iterated past it
            if (cmpMethod.IsSynthesized && !_generatedFunctions.Contains(item: cmpFuncName))
            {
                _generatedFunctions.Add(item: cmpFuncName);
                _generatedFunctionDefs.Add(item: cmpFuncName);
                EmitSynthesizedCmp(routine: cmpMethod, funcName: cmpFuncName);
            }
            else
            {
                GenerateFunctionDeclaration(routine: cmpMethod);
            }
        }
        else
        {
            cmpFuncName = Q(name: $"{routine.OwnerType.Name}.$cmp");
        }

        EmitLine(sb: _functionDefinitions,
            line: $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions,
            line: $"  %cmp = call i64 @{cmpFuncName}({meType} %me, {youType} %{youName})");
        EmitLine(sb: _functionDefinitions,
            line: $"  %result = icmp {cmpOp} i64 %cmp, {resolvedTag}");
        EmitLine(sb: _functionDefinitions, line: "  ret i1 %result");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

}
