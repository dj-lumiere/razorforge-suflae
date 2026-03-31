namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        // Determine element type from ResolvedType or first element
        TypeInfo? listType = list.ResolvedType;
        TypeInfo? elemType = null;

        if (listType is EntityTypeInfo entity && entity.TypeArguments.Count > 0)
        {
            elemType = entity.TypeArguments[index: 0];
        }
        else if (list.Elements.Count > 0)
        {
            elemType = GetExpressionType(expr: list.Elements[index: 0]);
        }

        string elemLLVMType = elemType != null
            ? GetLLVMType(type: elemType)
            : "i64";

        // Look up List type and its constructor/add_last method
        string listTypeName = listType != null
            ? listType.Name
            : $"List[{elemType?.Name ?? "S64"}]";
        // mangledListType is no longer needed — list type name used directly via Q()

        // Allocate the list via constructor or rf_alloc
        // Try to find a $create or use a fallback allocation
        string createName = $"{listTypeName}.$create";
        RoutineInfo? createMethod = _registry.LookupRoutine(fullName: createName);
        string entityTypeName = listType is EntityTypeInfo eti
            ? GetEntityTypeName(entity: eti)
            : $"%\"Entity.{listTypeName}\"";

        string listPtr;
        if (createMethod != null)
        {
            string mangledCreate = MangleFunctionName(routine: createMethod);
            listPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {listPtr} = call ptr @{mangledCreate}()");
        }
        else
        {
            // Fallback: allocate collection header: { ptr data, i64 count, i64 capacity }
            listPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 {_collectionHeaderSizeBytes})");

            // Initialize: data = alloc(n * elem_size), count = 0, capacity = element count
            // Entity layout is { ptr (data), i64 (count), i64 (capacity) }
            int elemSize = elemType != null
                ? GetTypeSize(type: elemType)
                : 8;
            long capacity = Math.Max(val1: list.Elements.Count, val2: 4);
            string dataPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {capacity * elemSize})");
            string dataPtrSlot = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {dataPtrSlot} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

            string countPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {countPtr} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store i64 0, ptr {countPtr}");

            string capPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {capPtr} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 2");
            EmitLine(sb: sb, line: $"  store i64 {capacity}, ptr {capPtr}");
        }

        // Add each element via add_last or direct store
        string addLastName = $"{listTypeName}.add_last";
        RoutineInfo? addLastMethod = _registry.LookupRoutine(fullName: addLastName);

        if (addLastMethod != null)
        {
            string mangledAddLast = MangleFunctionName(routine: addLastMethod);
            foreach (Expression elem in list.Elements)
            {
                string elemValue = EmitExpression(sb: sb, expr: elem);
                EmitLine(sb: sb,
                    line:
                    $"  call void @{mangledAddLast}(ptr {listPtr}, {elemLLVMType} {elemValue})");
            }
        }
        else
        {
            // Fallback: direct store into data buffer
            // Load data ptr (field 0), then GEP + store for each element
            string dataPtrSlot2 = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {dataPtrSlot2} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 0");
            string dataBase = NextTemp();
            EmitLine(sb: sb, line: $"  {dataBase} = load ptr, ptr {dataPtrSlot2}");

            for (int i = 0; i < list.Elements.Count; i++)
            {
                string elemValue = EmitExpression(sb: sb, expr: list.Elements[index: i]);
                string elemPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {elemPtr} = getelementptr {elemLLVMType}, ptr {dataBase}, i64 {i}");
                EmitLine(sb: sb, line: $"  store {elemLLVMType} {elemValue}, ptr {elemPtr}");
            }

            // Update count (field 1)
            string countPtr2 = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {countPtr2} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store i64 {list.Elements.Count}, ptr {countPtr2}");
        }

        return listPtr;
    }

    private string EmitSetLiteral(StringBuilder sb, SetLiteralExpression set)
    {
        TypeInfo? setType = set.ResolvedType;
        string setTypeName = setType?.Name ?? "Set";

        string setPtr = EmitCollectionCreate(sb: sb, resolvedType: setType, typeName: setTypeName);

        // Add each element via .add(element)
        foreach (Expression elem in set.Elements)
        {
            string elemValue = EmitExpression(sb: sb, expr: elem);
            TypeInfo? elemType = GetExpressionType(expr: elem);
            string elemLLVMType = elemType != null
                ? GetLLVMType(type: elemType)
                : "i64";

            string addName = $"{setTypeName}.add";
            RoutineInfo? addMethod = _registry.LookupRoutine(fullName: addName);
            if (addMethod != null)
            {
                string mangledAdd = MangleFunctionName(routine: addMethod);
                EmitLine(sb: sb,
                    line: $"  call i1 @{mangledAdd}(ptr {setPtr}, {elemLLVMType} {elemValue})");
            }
        }

        return setPtr;
    }

    private string EmitDictLiteral(StringBuilder sb, DictLiteralExpression dict)
    {
        TypeInfo? dictType = dict.ResolvedType;
        string dictTypeName = dictType?.Name ?? "Dict";

        string dictPtr =
            EmitCollectionCreate(sb: sb, resolvedType: dictType, typeName: dictTypeName);

        // Add each pair via .add(key, value)
        foreach ((Expression key, Expression value) in dict.Pairs)
        {
            string keyValue = EmitExpression(sb: sb, expr: key);
            string valValue = EmitExpression(sb: sb, expr: value);

            TypeInfo? keyType = GetExpressionType(expr: key);
            TypeInfo? valueType = GetExpressionType(expr: value);
            string keyLLVMType = keyType != null
                ? GetLLVMType(type: keyType)
                : "i64";
            string valueLLVMType = valueType != null
                ? GetLLVMType(type: valueType)
                : "i64";

            string addName = $"{dictTypeName}.add";
            RoutineInfo? addMethod = _registry.LookupRoutine(fullName: addName);
            if (addMethod != null)
            {
                string mangledAdd = MangleFunctionName(routine: addMethod);
                EmitLine(sb: sb,
                    line:
                    $"  call i1 @{mangledAdd}(ptr {dictPtr}, {keyLLVMType} {keyValue}, {valueLLVMType} {valValue})");
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

        // Look up add_last on List generic definition
        RoutineInfo? addLast = _registry.LookupRoutine(fullName: $"{listType.Name}.add_last") ??
                               _registry.LookupRoutine(fullName: "List.add_last");
        if (addLast == null)
        {
            return;
        }

        // Handle monomorphization
        string mangledAdd;
        if (listType.IsGenericResolution)
        {
            mangledAdd = Q(name: $"{listType.FullName}.add_last");
            RecordMonomorphization(mangledName: mangledAdd,
                genericMethod: addLast,
                resolvedOwnerType: listType);
        }
        else
        {
            mangledAdd = MangleFunctionName(routine: addLast);
        }

        // Declare add_last if needed
        string elemLlvm = GetLLVMType(type: elemType);
        if (!_generatedFunctions.Contains(item: mangledAdd))
        {
            EmitLine(sb: _functionDeclarations,
                line: $"declare void @{mangledAdd}(ptr, {elemLlvm})");
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
                newArgTypes.Add(item: GetLLVMType(type: param.Type));
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

        // Look up $create on the resolved type first, then fall back to generic definition
        string createName = $"{resolvedType.Name}.$create";
        RoutineInfo? creator =
            _registry.LookupRoutineOverload(fullName: createName, argTypes: new List<TypeInfo>());
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
                creator = _registry.LookupRoutineOverload(fullName: genCreateName,
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

            // Ensure declared
            if (!_generatedFunctions.Contains(item: funcName))
            {
                EmitLine(sb: _functionDeclarations, line: $"declare ptr @{funcName}()");
                _generatedFunctions.Add(item: funcName);
            }

            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
            return result;
        }

        // Last resort: direct allocation with collection header size
        string ptr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {ptr} = call ptr @rf_allocate_dynamic(i64 {_collectionHeaderSizeBytes})");
        return ptr;
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

        string llvmType = GetLLVMType(type: entryType);
        string tmp1 = NextTemp();
        string tmp2 = NextTemp();
        TypeInfo? keyType = GetExpressionType(expr: dictEntry.Key);
        TypeInfo? valueType = GetExpressionType(expr: dictEntry.Value);
        string keyLlvm = keyType != null
            ? GetLLVMType(type: keyType)
            : "i64";
        string valLlvm = valueType != null
            ? GetLLVMType(type: valueType)
            : "i64";

        EmitLine(sb: sb, line: $"  {tmp1} = insertvalue {llvmType} undef, {keyLlvm} {keyVal}, 0");
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
            string llvmType = GetLLVMType(type: resolvedType);
            string current = "zeroinitializer";
            for (int i = 0; i < arguments.Count; i++)
            {
                string elemVal = EmitExpression(sb: sb, expr: arguments[index: i]);
                TypeInfo? elemType = GetExpressionType(expr: arguments[index: i]);
                string elemLlvm = elemType != null
                    ? GetLLVMType(type: elemType)
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
            string llvmType = GetLLVMType(type: resolvedType);
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

        // PriorityQueue[TPriority, TElement]: $create() + add(element, priority) from tuple args
        if (baseName == "PriorityQueue")
        {
            return EmitPriorityQueueLiteral(sb: sb,
                resolvedType: resolvedType,
                typeName: typeName,
                arguments: arguments);
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

        // Look up the add method
        string fullAddName = $"{typeName}.{addMethodName}";
        RoutineInfo? addMethod = _registry.LookupRoutine(fullName: fullAddName);

        // Fall back to generic definition
        if (addMethod == null)
        {
            TypeInfo? genericDef = resolvedType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                _ => null
            };
            if (genericDef != null)
            {
                string genAddName = $"{genericDef.Name}.{addMethodName}";
                addMethod = _registry.LookupRoutine(fullName: genAddName);
            }
        }

        if (addMethod == null)
        {
            return collectionPtr;
        }

        // Determine mangled name and handle monomorphization
        string mangledAdd;
        if (resolvedType.IsGenericResolution)
        {
            mangledAdd = Q(name: $"{resolvedType.FullName}.{addMethodName}");
            RecordMonomorphization(mangledName: mangledAdd,
                genericMethod: addMethod,
                resolvedOwnerType: resolvedType);
        }
        else
        {
            mangledAdd = MangleFunctionName(routine: addMethod);
        }

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
                        ? GetLLVMType(type: keyType)
                        : "i64";
                    string valLlvm = valueType != null
                        ? GetLLVMType(type: valueType)
                        : "i64";

                    // Declare the add function if not yet declared
                    if (!_generatedFunctions.Contains(item: mangledAdd))
                    {
                        EmitLine(sb: _functionDeclarations,
                            line: $"declare i1 @{mangledAdd}(ptr, {keyLlvm}, {valLlvm})");
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
                    ? GetLLVMType(type: elemType)
                    : "i64";

                // Declare the add function if not yet declared
                if (!_generatedFunctions.Contains(item: mangledAdd))
                {
                    // add returns Bool for Set/SortedSet, void for others
                    string retType = baseName is "Set" or "SortedSet"
                        ? "i1"
                        : "void";
                    EmitLine(sb: _functionDeclarations,
                        line: $"declare {retType} @{mangledAdd}(ptr, {elemLlvm})");
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
        string llvmType = GetLLVMType(type: resolvedType);
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
    /// Emits PriorityQueue literal constructor: PriorityQueue[S32, Text]((1, "high"), (10, "low"))
    /// Each argument is a tuple (priority, element) unpacked into add(element: element, priority: priority).
    /// </summary>
    private string EmitPriorityQueueLiteral(StringBuilder sb, TypeInfo resolvedType,
        string typeName, List<Expression> arguments)
    {
        string pqPtr =
            EmitCollectionCreate(sb: sb, resolvedType: resolvedType, typeName: typeName);

        // Look up add(element, priority) method
        string addName = $"{typeName}.add";
        RoutineInfo? addMethod = _registry.LookupRoutine(fullName: addName);

        if (addMethod == null)
        {
            TypeInfo? genericDef = resolvedType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                _ => null
            };
            if (genericDef != null)
            {
                string genAddName = $"{genericDef.Name}.add";
                addMethod = _registry.LookupRoutine(fullName: genAddName);
            }
        }

        if (addMethod == null)
        {
            return pqPtr;
        }

        string mangledAdd;
        if (resolvedType.IsGenericResolution)
        {
            mangledAdd = Q(name: $"{resolvedType.FullName}.add");
            RecordMonomorphization(mangledName: mangledAdd,
                genericMethod: addMethod,
                resolvedOwnerType: resolvedType);
        }
        else
        {
            mangledAdd = MangleFunctionName(routine: addMethod);
        }

        // Resolve priority and element types from type arguments
        // PriorityQueue[TPriority, TElement] — args are (priority, element) tuples
        TypeInfo? priorityType = resolvedType.TypeArguments?.Count >= 1
            ? resolvedType.TypeArguments[index: 0]
            : null;
        TypeInfo? elementType = resolvedType.TypeArguments?.Count >= 2
            ? resolvedType.TypeArguments[index: 1]
            : null;
        string priorityLlvm = priorityType != null
            ? GetLLVMType(type: priorityType)
            : "i32";
        string elementLlvm = elementType != null
            ? GetLLVMType(type: elementType)
            : "ptr";

        foreach (Expression arg in arguments)
        {
            string? priorityVal = null;
            string? elementVal = null;

            // DictEntryLiteralExpression: PriorityQueue(1: "high", 2: "low")
            if (arg is DictEntryLiteralExpression dictEntry)
            {
                priorityVal = EmitExpression(sb: sb, expr: dictEntry.Key);
                elementVal = EmitExpression(sb: sb, expr: dictEntry.Value);
            }
            // TupleLiteralExpression: PriorityQueue((1, "high"), (2, "low"))
            else if (arg is TupleLiteralExpression tuple && tuple.Elements.Count == 2)
            {
                priorityVal = EmitExpression(sb: sb, expr: tuple.Elements[index: 0]);
                elementVal = EmitExpression(sb: sb, expr: tuple.Elements[index: 1]);
            }

            if (priorityVal != null && elementVal != null)
            {
                // add(element: TElement, priority: TPriority)
                if (!_generatedFunctions.Contains(item: mangledAdd))
                {
                    EmitLine(sb: _functionDeclarations,
                        line: $"declare void @{mangledAdd}(ptr, {elementLlvm}, {priorityLlvm})");
                    _generatedFunctions.Add(item: mangledAdd);
                }

                EmitLine(sb: sb,
                    line:
                    $"  call void @{mangledAdd}(ptr {pqPtr}, {elementLlvm} {elementVal}, {priorityLlvm} {priorityVal})");
            }
        }

        return pqPtr;
    }


    /// <summary>
    /// Emits a flags test expression: x is FLAG, x isnot FLAG, x isonly FLAG.
    /// FlagsTestExpression has Subject, Kind, TestFlags, Connective, and ExcludedFlags.
    /// </summary>
}
