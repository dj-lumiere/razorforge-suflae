namespace Compiler.CodeGen;

using System.Text;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for collection and aggregate literals.
/// </summary>
public partial class LlvmCodeGenerator
{
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        TypeInfo? listType = list.ResolvedType;
        TypeInfo? elemType = null;
        if (listType != null)
        {
            string baseName = GetGenericBaseName(type: listType) ?? listType.Name;
            if (baseName == "ValueList")
            {
                return EmitCollectionLiteralConstructor(sb: sb,
                    resolvedType: listType,
                    arguments: list.Elements);
            }

            if (baseName == "ValueBitList")
            {
                return EmitCollectionLiteralConstructor(sb: sb,
                    resolvedType: listType,
                    arguments: list.Elements);
            }

            if (listType.TypeArguments?.Count > 0)
            {
                elemType = listType.TypeArguments[index: 0];
            }
            else if (baseName is "BitList" or "ValueBitList")
            {
                elemType = _registry.LookupType(name: "Bool");
            }
        }

        elemType ??= list.Elements.Count > 0
            ? GetExpressionType(expr: list.Elements[index: 0])
            : null;

        string listTypeName = listType != null
            ? listType.Name
            : $"List[{elemType?.Name ?? "S64"}]";
        string listPtr = EmitCollectionCreate(sb: sb, resolvedType: listType, typeName: listTypeName);
        string elemLlvmType = elemType != null
            ? GetLlvmType(type: elemType)
            : "i64";

        ResolvedMethod? resolvedAdd = listType != null
            ? ResolveMethod(receiverType: listType, methodName: "add_last") ??
              ResolveMethod(receiverType: listType, methodName: "add")
            : null;

        if (resolvedAdd != null)
        {
            string mangledAdd = resolvedAdd.MangledName;
            string addReturnType = resolvedAdd.Routine.ReturnType != null
                ? GetLlvmType(type: ResolveTypeSubstitution(type: resolvedAdd.Routine.ReturnType))
                : "void";
            if (!_generatedFunctions.Contains(item: mangledAdd))
            {
                _rfFunctionDeclarations[key: mangledAdd] =
                    $"declare {addReturnType} @{mangledAdd}(ptr, {elemLlvmType})";
                _generatedFunctions.Add(item: mangledAdd);
            }

            foreach (Expression elem in list.Elements)
            {
                string elemValue = EmitExpression(sb: sb, expr: elem);
                if (addReturnType == "void")
                {
                    EmitLine(sb: sb,
                        line: $"  call void @{mangledAdd}(ptr {listPtr}, {elemLlvmType} {elemValue})");
                }
                else
                {
                    string ignored = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {ignored} = call {addReturnType} @{mangledAdd}(ptr {listPtr}, {elemLlvmType} {elemValue})");
                }
            }
        }

        return listPtr;
    }

    private string EmitSetLiteral(StringBuilder sb, SetLiteralExpression set)
    {
        TypeInfo? setType = set.ResolvedType;
        string setTypeName = setType?.Name ?? "Set";

        string setPtr = EmitCollectionCreate(sb: sb, resolvedType: setType, typeName: setTypeName);

        // Add each element via .add(element)
        ResolvedMethod? resolvedAdd = setType != null
            ? ResolveMethod(receiverType: setType, methodName: "add")
            : null;
        foreach (Expression elem in set.Elements)
        {
            string elemValue = EmitExpression(sb: sb, expr: elem);
            TypeInfo? elemType = GetExpressionType(expr: elem);
            string elemLlvmType = elemType != null
                ? GetLlvmType(type: elemType)
                : "i64";

            if (resolvedAdd != null)
            {
                string mangledAdd = resolvedAdd.MangledName;
                if (!_generatedFunctions.Contains(item: mangledAdd))
                {
                    _rfFunctionDeclarations[key: mangledAdd] =
                        $"declare i1 @{mangledAdd}(ptr, {elemLlvmType})";
                    _generatedFunctions.Add(item: mangledAdd);
                }

                EmitLine(sb: sb,
                    line: $"  call i1 @{mangledAdd}(ptr {setPtr}, {elemLlvmType} {elemValue})");
            }
        }

        return setPtr;
    }

    private string EmitDictLiteral(StringBuilder sb, DictLiteralExpression dict)
    {
        TypeInfo? dictType = dict.ResolvedType;
        string dictTypeName = dictType?.Name ?? "Dict";

        if (dictType != null && (GetGenericBaseName(type: dictType) ?? dictType.Name) ==
            "PriorityQueue")
        {
            var entryArgs = dict.Pairs.Select(selector: pair =>
                    (Expression)new DictEntryLiteralExpression(Key: pair.Key,
                        Value: pair.Value,
                        Location: pair.Key.Location))
                .ToList();
            return EmitPriorityQueueLiteral(sb: sb,
                resolvedType: dictType,
                typeName: dictTypeName,
                arguments: entryArgs);
        }

        string dictPtr =
            EmitCollectionCreate(sb: sb, resolvedType: dictType, typeName: dictTypeName);

        // Add each pair via .add(key, value)
        ResolvedMethod? resolvedAdd = dictType != null
            ? ResolveMethod(receiverType: dictType, methodName: "add")
            : null;
        foreach ((Expression key, Expression value) in dict.Pairs)
        {
            string keyValue = EmitExpression(sb: sb, expr: key);
            string valValue = EmitExpression(sb: sb, expr: value);

            TypeInfo? keyType = GetExpressionType(expr: key);
            TypeInfo? valueType = GetExpressionType(expr: value);
            string keyLlvmType = keyType != null
                ? GetLlvmType(type: keyType)
                : "i64";
            string valueLlvmType = valueType != null
                ? GetLlvmType(type: valueType)
                : "i64";

            if (resolvedAdd != null)
            {
                string mangledAdd = resolvedAdd.MangledName;
                if (!_generatedFunctions.Contains(item: mangledAdd))
                {
                    _rfFunctionDeclarations[key: mangledAdd] =
                        $"declare i1 @{mangledAdd}(ptr, {keyLlvmType}, {valueLlvmType})";
                    _generatedFunctions.Add(item: mangledAdd);
                }

                EmitLine(sb: sb,
                    line:
                    $"  call i1 @{mangledAdd}(ptr {dictPtr}, {keyLlvmType} {keyValue}, {valueLlvmType} {valValue})");
            }
        }

        return dictPtr;
    }

    /// <summary>
    /// C105: Packs variadic arguments into a List[T] at the call site.
    /// Modifies argValues/argTypes, replacing individual variadic args with a single list pointer.
    /// </summary>
    private void PackVariadicArgs(StringBuilder sb, RoutineInfo routine,
        List<Expression> arguments, ref List<string> argValues, ref List<string> argTypes,
        int argOffset)
    {
        ParameterInfo? varParam =
            routine.Parameters.FirstOrDefault(predicate: p => p.IsVariadicParam);
        if (varParam == null)
        {
            return;
        }

        int varIndex = varParam.Index;
        int packStart = varIndex + argOffset;

        // Determine element type from actual arguments or parameter type
        TypeInfo? elemType = null;
        int firstVarArgIdx = varIndex; // index into explicit arguments (not argValues)
        if (firstVarArgIdx < arguments.Count)
        {
            elemType = GetExpressionType(expr: arguments[index: firstVarArgIdx]);
        }

        // Fall back: extract T from List[T] parameter type
        if (elemType == null && varParam.Type is { TypeArguments: { Count: > 0 } })
        {
            elemType = varParam.Type.TypeArguments[index: 0];
            if (elemType is GenericParameterTypeInfo && _typeSubstitutions != null &&
                _typeSubstitutions.TryGetValue(key: elemType.Name, value: out TypeInfo? resolved))
            {
                elemType = resolved;
            }
        }

        if (elemType == null)
        {
            return;
        }

        // Resolve List[elemType]
        TypeInfo? listGenDef = _registry.LookupType(name: "List");
        if (listGenDef == null)
        {
            return;
        }

        TypeInfo listType =
            _registry.GetOrCreateResolution(genericDef: listGenDef, typeArguments: [elemType]);

        // Create empty List[elemType]
        string listPtr =
            EmitCollectionCreate(sb: sb, resolvedType: listType, typeName: listType.Name);

        // Look up add_last on List type (handles generic resolution automatically)
        ResolvedMethod? resolvedAddLast = ResolveMethod(receiverType: listType, methodName: "add_last");
        if (resolvedAddLast == null)
        {
            return;
        }

        string mangledAdd = resolvedAddLast.MangledName;

        // Declare add_last if needed
        string elemLlvm = GetLlvmType(type: elemType);
        if (!_generatedFunctions.Contains(item: mangledAdd))
        {
            _rfFunctionDeclarations[key: mangledAdd] =
                $"declare void @{mangledAdd}(ptr, {elemLlvm})";
            _generatedFunctions.Add(item: mangledAdd);
        }

        // Determine how many trailing (non-variadic) params follow the varargs param.
        // Named args matching trailing params are NOT varargs.
        int trailingParamCount = routine.Parameters.Count - varIndex - 1;
        var trailingParamNames = new HashSet<string>();
        for (int i = varIndex + 1; i < routine.Parameters.Count; i++)
        {
            trailingParamNames.Add(item: routine.Parameters[index: i].Name);
        }

        // Count explicit named args that match trailing params
        int namedTrailingCount = arguments.Count(predicate: a =>
            a is NamedArgumentExpression na && trailingParamNames.Contains(item: na.Name));
        int packEnd = argValues.Count - namedTrailingCount;

        // Emit add_last for each variadic argument
        for (int i = packStart; i < packEnd; i++)
        {
            EmitLine(sb: sb,
                line:
                $"  call void @{mangledAdd}(ptr {listPtr}, {elemLlvm} {argValues[index: i]})");
        }

        // Build new argValues: [pre-varargs] + [listPtr] + [trailing defaults]
        var newArgValues = argValues.Take(count: packStart)
                                    .ToList();
        newArgValues.Add(item: listPtr);
        var newArgTypes = argTypes.Take(count: packStart)
                                  .ToList();
        newArgTypes.Add(item: "ptr");

        // Append trailing args if they were already supplied
        for (int i = packEnd; i < argValues.Count; i++)
        {
            newArgValues.Add(item: argValues[index: i]);
            newArgTypes.Add(item: argTypes[index: i]);
        }

        // Supply defaults for trailing params not yet covered
        int suppliedTrailing = argValues.Count - packEnd;
        for (int i = varIndex + 1 + suppliedTrailing; i < routine.Parameters.Count; i++)
        {
            ParameterInfo param = routine.Parameters[index: i];
            if (param.HasDefaultValue)
            {
                string value = EmitExpression(sb: sb, expr: param.DefaultValue!);
                newArgValues.Add(item: value);
                newArgTypes.Add(item: GetLlvmType(type: param.Type));
            }
        }

        argValues = newArgValues;
        argTypes = newArgTypes;
    }

    /// <summary>
    /// Emits a zero-arg $create() call for a collection type, handling monomorphization.
    /// </summary>
    private string EmitCollectionCreate(StringBuilder sb, TypeInfo? resolvedType, string typeName)
    {
        if (resolvedType == null)
        {
            return "null";
        }

        // Try ResolveMethod first — handles lookup + mangle + monomorphization
        ResolvedMethod? resolved = ResolveMethod(receiverType: resolvedType, methodName: "$create");

        // ResolveMethod uses LookupMethod which may return a $create with parameters;
        // we only want the zero-arg overload, so verify
        if (resolved != null && resolved.Routine.Parameters.Count > 0)
        {
            resolved = null;
        }

        // Fall back to overload-based lookup (zero-arg $create via registry)
        if (resolved == null)
        {
            string createName = $"{resolvedType.Name}.$create";
            RoutineInfo? creator =
                _registry.LookupRoutineOverload(baseName: createName, argTypes: new List<TypeInfo>());
            if (creator != null && creator.Parameters.Count > 0)
            {
                creator = null;
            }

            // Fall back to generic definition's $create
            if (creator == null)
            {
                TypeInfo? genericDef = resolvedType switch
                {
                    EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                    RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                    _ => null
                };
                if (genericDef != null)
                {
                    string genCreateName = $"{genericDef.Name}.$create";
                    creator = _registry.LookupRoutineOverload(baseName: genCreateName,
                        argTypes: new List<TypeInfo>());
                    if (creator != null && creator.Parameters.Count > 0)
                    {
                        creator = null;
                    }
                }
            }

            if (creator != null)
            {
                string funcName;
                if (resolvedType.IsGenericResolution)
                {
                    funcName = Q(name: $"{resolvedType.FullName}.$create");
                    RecordMonomorphization(mangledName: funcName,
                        genericMethod: creator,
                        resolvedOwnerType: resolvedType);
                }
                else
                {
                    funcName = MangleFunctionName(routine: creator);
                }

                if (!_generatedFunctions.Contains(item: funcName))
                {
                    GenerateFunctionDeclaration(routine: creator, nameOverride: funcName);
                }

                string result = NextTemp();
                EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
                return result;
            }
        }
        else
        {
            string funcName = resolved.MangledName;
            if (!_generatedFunctions.Contains(item: funcName))
            {
                GenerateFunctionDeclaration(routine: resolved.Routine, nameOverride: funcName);
            }

            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
            return result;
        }

        // TODO(C41): this fallback has been replaced with a hard error.
        // Collection $create must always be resolvable from stdlib.
        throw new InvalidOperationException(
            $"No '$create' routine found for collection type '{resolvedType.Name}'. " +
            "All collection types must have a registered '$create' body in the stdlib.");
    }

    /// <summary>
    /// Emits a standalone DictEntry literal (key:value used outside a collection constructor).
    /// Constructs a DictEntry[K, V] record via insertvalue.
    /// </summary>
    private string EmitDictEntryLiteral(StringBuilder sb, DictEntryLiteralExpression dictEntry)
    {
        string keyVal = EmitExpression(sb: sb, expr: dictEntry.Key);
        string valVal = EmitExpression(sb: sb, expr: dictEntry.Value);

        TypeInfo? entryType = dictEntry.ResolvedType;
        if (entryType == null)
        {
            return "undef";
        }

        string llvmType = GetLlvmType(type: entryType);
        string tmp1 = NextTemp();
        string tmp2 = NextTemp();
        TypeInfo? keyType = GetExpressionType(expr: dictEntry.Key);
        TypeInfo? valueType = GetExpressionType(expr: dictEntry.Value);
        string keyLlvm = keyType != null
            ? GetLlvmType(type: keyType)
            : "i64";
        string valLlvm = valueType != null
            ? GetLlvmType(type: valueType)
            : "i64";

        EmitLine(sb: sb, line: $"  {tmp1} = insertvalue {llvmType} zeroinitializer, {keyLlvm} {keyVal}, 0");
        EmitLine(sb: sb, line: $"  {tmp2} = insertvalue {llvmType} {tmp1}, {valLlvm} {valVal}, 1");
        return tmp2;
    }

    /// <summary>
    /// Emits a collection literal constructor: List(1, 2, 3), Set(1, 2), Dict(1:2), etc.
    /// Creates collection via $create(), then adds elements via add/add_last.
    /// For ValueList/ValueBitList, uses insertvalue for inline array construction.
    /// </summary>
    private string EmitCollectionLiteralConstructor(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string typeName = resolvedType.Name;
        // Extract base type name (e.g., "List" from "List[S64]")
        string baseName = GetGenericBaseName(type: resolvedType) ?? typeName;

        // ValueList[T, N]: inline array construction via insertvalue
        if (baseName == "ValueList")
        {
            string llvmType = GetLlvmType(type: resolvedType);
            string current = "zeroinitializer";
            for (int i = 0; i < arguments.Count; i++)
            {
                string elemVal = EmitExpression(sb: sb, expr: arguments[index: i]);
                TypeInfo? elemType = GetExpressionType(expr: arguments[index: i]);
                string elemLlvm = elemType != null
                    ? GetLlvmType(type: elemType)
                    : "i64";
                string next = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {next} = insertvalue {llvmType} {current}, {elemLlvm} {elemVal}, {i}");
                current = next;
            }

            return current;
        }

        // ValueBitList[N]: inline bit-packed array construction
        if (baseName == "ValueBitList")
        {
            string llvmType = GetLlvmType(type: resolvedType);
            // Calculate byte count from number of bits
            int bitCount = arguments.Count;
            int byteCount = (bitCount + 7) / 8;

            // Pack bits into bytes, then insertvalue each byte
            string current = "zeroinitializer";
            for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
            {
                int byteVal = 0;
                for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
                {
                    // Evaluate the boolean argument
                    // For literal bools, we can read the value directly
                    if (arguments[index: byteIdx * 8 + bitIdx] is LiteralExpression
                        {
                            Value: true
                        })
                    {
                        byteVal |= 1 << bitIdx;
                    }
                    else if (arguments[index: byteIdx * 8 + bitIdx] is LiteralExpression
                             {
                                 Value: false
                             })
                    {
                        // bit is already 0
                    }
                    else
                    {
                        // Non-literal: evaluate at runtime and OR into byte
                        // For simplicity, fall back to runtime construction for non-literal bools
                        return EmitValueBitListRuntime(sb: sb,
                            resolvedType: resolvedType,
                            arguments: arguments);
                    }
                }

                string next = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {next} = insertvalue {llvmType} {current}, i8 {byteVal}, {byteIdx}");
                current = next;
            }

            return current;
        }

        // Entity collections: $create() + add/add_last calls
        string collectionPtr =
            EmitCollectionCreate(sb: sb, resolvedType: resolvedType, typeName: typeName);

        // Determine the add method name
        string addMethodName;
        bool isMapType = baseName is "Dict" or "SortedDict";
        bool isSequenceType = baseName is "List" or "Deque" or "BitList";

        if (isSequenceType)
        {
            addMethodName = "add_last";
        }
        else
        {
            addMethodName = "add";
        }

        // Look up the add method (handles generic resolution automatically)
        ResolvedMethod? resolvedAdd = ResolveMethod(receiverType: resolvedType, methodName: addMethodName);

        if (resolvedAdd == null)
        {
            return collectionPtr;
        }

        string mangledAdd = resolvedAdd.MangledName;

        // Emit add calls for each element
        if (isMapType)
        {
            // Dict/SortedDict: extract key and value from DictEntry arguments
            foreach (Expression arg in arguments)
            {
                if (arg is DictEntryLiteralExpression entry)
                {
                    string keyVal = EmitExpression(sb: sb, expr: entry.Key);
                    string valVal = EmitExpression(sb: sb, expr: entry.Value);
                    TypeInfo? keyType = GetExpressionType(expr: entry.Key);
                    TypeInfo? valueType = GetExpressionType(expr: entry.Value);
                    string keyLlvm = keyType != null
                        ? GetLlvmType(type: keyType)
                        : "i64";
                    string valLlvm = valueType != null
                        ? GetLlvmType(type: valueType)
                        : "i64";

                    // Declare the add function if not yet declared
                    if (!_generatedFunctions.Contains(item: mangledAdd))
                    {
                        _rfFunctionDeclarations[key: mangledAdd] =
                            $"declare i1 @{mangledAdd}(ptr, {keyLlvm}, {valLlvm})";
                        _generatedFunctions.Add(item: mangledAdd);
                    }

                    EmitLine(sb: sb,
                        line:
                        $"  call i1 @{mangledAdd}(ptr {collectionPtr}, {keyLlvm} {keyVal}, {valLlvm} {valVal})");
                }
            }
        }
        else
        {
            // Sequence/unique: add each element
            foreach (Expression arg in arguments)
            {
                string elemVal = EmitExpression(sb: sb, expr: arg);
                TypeInfo? elemType = GetExpressionType(expr: arg);
                string elemLlvm = elemType != null
                    ? GetLlvmType(type: elemType)
                    : "i64";

                // Declare the add function if not yet declared
                if (!_generatedFunctions.Contains(item: mangledAdd))
                {
                    // add returns Bool for Set/SortedSet, void for others
                    string retType = baseName is "Set" or "SortedSet"
                        ? "i1"
                        : "void";
                    _rfFunctionDeclarations[key: mangledAdd] =
                        $"declare {retType} @{mangledAdd}(ptr, {elemLlvm})";
                    _generatedFunctions.Add(item: mangledAdd);
                }

                string retType2 = baseName is "Set" or "SortedSet"
                    ? "i1"
                    : "void";
                string callPrefix = retType2 == "void"
                    ? ""
                    : $"  {NextTemp()} = ";
                // For void returns, we just call. For non-void, we discard the result.
                if (retType2 == "void")
                {
                    EmitLine(sb: sb,
                        line:
                        $"  call void @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
                }
                else
                {
                    EmitLine(sb: sb,
                        line:
                        $"  call i1 @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
                }
            }
        }

        return collectionPtr;
    }

    /// <summary>
    /// Runtime fallback for ValueBitList construction when arguments are non-literal.
    /// Evaluates each bool at runtime and packs into bytes.
    /// </summary>
    private string EmitValueBitListRuntime(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string llvmType = GetLlvmType(type: resolvedType);
        int bitCount = arguments.Count;
        int byteCount = (bitCount + 7) / 8;

        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            // Start with 0, OR in each bit
            string byteAccum = "0";
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                string boolVal =
                    EmitExpression(sb: sb, expr: arguments[index: byteIdx * 8 + bitIdx]);
                // zext i1 to i8, shift left by bitIdx, OR into accumulator
                string extended = NextTemp();
                EmitLine(sb: sb, line: $"  {extended} = zext i1 {boolVal} to i8");
                if (bitIdx > 0)
                {
                    string shifted = NextTemp();
                    EmitLine(sb: sb, line: $"  {shifted} = shl i8 {extended}, {bitIdx}");
                    extended = shifted;
                }

                string ored = NextTemp();
                EmitLine(sb: sb, line: $"  {ored} = or i8 {byteAccum}, {extended}");
                byteAccum = ored;
            }

            string next = NextTemp();
            EmitLine(sb: sb,
                line: $"  {next} = insertvalue {llvmType} {current}, i8 {byteAccum}, {byteIdx}");
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Emits a dict-literal-backed PriorityQueue constructor:
    /// <c>var items: PriorityQueue[S32, Text] = {1: "high", 10: "low"}</c>.
    /// </summary>
    private string EmitPriorityQueueLiteral(StringBuilder sb, TypeInfo resolvedType,
        string typeName, List<Expression> arguments)
    {
        string pqPtr =
            EmitCollectionCreate(sb: sb, resolvedType: resolvedType, typeName: typeName);

        // Look up add(element, priority) method (handles generic resolution automatically)
        ResolvedMethod? resolvedAdd = ResolveMethod(receiverType: resolvedType, methodName: "add");

        if (resolvedAdd == null)
        {
            return pqPtr;
        }

        string mangledAdd = resolvedAdd.MangledName;

        // Resolve priority and element types from type arguments
        // PriorityQueue[TPriority, TElement] — args are (priority, element) tuples
        TypeInfo? priorityType = resolvedType.TypeArguments?.Count >= 1
            ? resolvedType.TypeArguments[index: 0]
            : null;
        TypeInfo? elementType = resolvedType.TypeArguments?.Count >= 2
            ? resolvedType.TypeArguments[index: 1]
            : null;
        string priorityLlvm = priorityType != null
            ? GetLlvmType(type: priorityType)
            : "i32";
        string elementLlvm = elementType != null
            ? GetLlvmType(type: elementType)
            : "ptr";

        foreach (Expression arg in arguments)
        {
            if (arg is DictEntryLiteralExpression dictEntry)
            {
                string priorityVal = EmitExpression(sb: sb, expr: dictEntry.Key);
                string elementVal = EmitExpression(sb: sb, expr: dictEntry.Value);

                // add(element: TElement, priority: TPriority)
                if (!_generatedFunctions.Contains(item: mangledAdd))
                {
                    _rfFunctionDeclarations[key: mangledAdd] =
                        $"declare void @{mangledAdd}(ptr, {elementLlvm}, {priorityLlvm})";
                    _generatedFunctions.Add(item: mangledAdd);
                }

                EmitLine(sb: sb,
                    line:
                    $"  call void @{mangledAdd}(ptr {pqPtr}, {elementLlvm} {elementVal}, {priorityLlvm} {priorityVal})");
            }
            else
            {
                throw new InvalidOperationException(
                    message: "PriorityQueue literal reached codegen without dict-entry lowering.");
            }
        }

        return pqPtr;
    }
}
