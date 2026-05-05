using System.Text;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation for collection literals and variadic argument packing.
/// Array[T,N] and BitArray[N] are emitted as inline insertvalue sequences here.
/// All other collection literals from [] / {} / {} syntax must be lowered by
/// ExpressionLoweringPass to CreatorExpression + add calls before reaching codegen.
/// The CollectionConstruction lowering kind (from explicit List(...) calls) still routes
/// through EmitCollectionLiteralConstructor for non-literal construction.
/// </summary>
public partial class LlvmCodeGenerator
{
    private static TypeInfo UnwrapCollectionStorageType(TypeInfo type)
    {
        TypeInfo current = type;
        while (current is WrapperTypeInfo wrapper && wrapper.Name == "Owned")
        {
            current = wrapper.InnerType;
        }

        return current;
    }

    /// <summary>
    /// Returns true if the type is Array[T,N] or BitArray[N] — the only list-literal types
    /// that remain in codegen as inline IR (insertvalue). All other collection types must be
    /// lowered to CreatorExpression + add calls by ExpressionLoweringPass.
    /// </summary>
    private static bool IsArrayOrBitArrayLiteral(TypeInfo? type)
    {
        if (type == null) return false;
        TypeInfo concrete = UnwrapCollectionStorageType(type);
        string baseName = GetGenericBaseName(type: concrete) ?? concrete.Name;
        return baseName is "Array" or "BitArray";
    }

    /// <summary>
    /// Emits an Array[T,N] or BitArray[N] literal using inline insertvalue IR.
    /// Guarded at the call site — only reached when IsArrayOrBitArrayLiteral is true.
    /// </summary>
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        TypeInfo concreteListType = UnwrapCollectionStorageType(list.ResolvedType!);
        return EmitCollectionLiteralConstructor(sb: sb, resolvedType: concreteListType,
            arguments: list.Elements);
    }

    /// <summary>
    /// Emits a collection literal constructor: Array[T,N] (insertvalue), BitArray[N] (bit packing),
    /// or entity collection (create + add calls).
    /// Called from EmitListLiteral (Array/BitArray only) and from the CollectionConstruction
    /// lowering kind path in EmitRoutineCall / EmitMemberRoutineCall.
    /// </summary>
    private string EmitCollectionLiteralConstructor(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string typeName = resolvedType.Name;
        string baseName = GetGenericBaseName(type: resolvedType) ?? typeName;

        switch (baseName)
        {
            // Array[T, N]: inline array construction via insertvalue
            case "Array":
            {
                string llvmType = GetLlvmType(type: resolvedType);
                string current = "zeroinitializer";
                for (int i = 0; i < arguments.Count; i++)
                {
                    string elemVal = EmitExpression(sb: sb, expr: arguments[index: i]);
                    TypeInfo? elemType = GetExpressionType(expr: arguments[index: i]);
                    string elemLlvm = elemType != null ? GetLlvmType(type: elemType) : "i64";
                    string next = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {next} = insertvalue {llvmType} {current}, {elemLlvm} {elemVal}, {i}");
                    current = next;
                }

                return current;
            }
            // BitArray[N]: inline bit-packed array construction
            case "BitArray":
            {
                string llvmType = GetLlvmType(type: resolvedType);
                int bitCount = arguments.Count;
                int byteCount = (bitCount + 7) / 8;

                string current = "zeroinitializer";
                for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
                {
                    int byteVal = 0;
                    bool allLiteral = true;
                    for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
                    {
                        if (arguments[index: byteIdx * 8 + bitIdx] is LiteralExpression { Value: true })
                            byteVal |= 1 << bitIdx;
                        else
                        {
                            allLiteral = false;
                            break;
                        }
                    }

                    if (!allLiteral)
                        return EmitBitArrayRuntime(sb: sb, resolvedType: resolvedType,
                            arguments: arguments);

                    string next = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {next} = insertvalue {llvmType} {current}, i8 {byteVal}, {byteIdx}");
                    current = next;
                }

                return current;
            }
        }

        // Entity collections (List, Set, Dict, etc.): $create() + add/add_last calls.
        // Reached only via CollectionConstruction lowering kind, not from ListLiteralExpression
        // (which is lowered by ExpressionLoweringPass for entity collection types).
        string collectionPtr =
            EmitCollectionCreate(sb: sb, resolvedType: resolvedType, typeName: typeName);

        string addMemberRoutineName;
        bool isMapType = baseName is "Dict" or "SortedDict";
        bool isSequenceType = baseName is "List" or "Deque" or "BitList";
        addMemberRoutineName = isSequenceType ? "add_last" : "add";

        ResolvedMemberRoutine? resolvedAdd = ResolveMemberRoutine(receiverType: resolvedType, methodName: addMemberRoutineName);
        if (resolvedAdd == null) return collectionPtr;

        string mangledAdd = resolvedAdd.MangledName;

        if (isMapType)
        {
            foreach (Expression arg in arguments)
            {
                if (arg is not DictEntryLiteralExpression entry)
                {
                    continue;
                }

                string keyVal = EmitExpression(sb: sb, expr: entry.Key);
                string valVal = EmitExpression(sb: sb, expr: entry.Value);
                TypeInfo? keyType = GetExpressionType(expr: entry.Key);
                TypeInfo? valueType = GetExpressionType(expr: entry.Value);
                string keyLlvm = keyType != null ? GetLlvmType(type: keyType) : "i64";
                string valLlvm = valueType != null ? GetLlvmType(type: valueType) : "i64";

                if (!_generatedRoutines.Contains(item: mangledAdd))
                {
                    _rfRoutineDeclarations[key: mangledAdd] =
                        $"declare i1 @{mangledAdd}(ptr, {keyLlvm}, {valLlvm})";
                    _generatedRoutines.Add(item: mangledAdd);
                }

                EmitLine(sb: sb,
                    line:
                    $"  call i1 @{mangledAdd}(ptr {collectionPtr}, {keyLlvm} {keyVal}, {valLlvm} {valVal})");
            }
        }
        else
        {
            foreach (Expression arg in arguments)
            {
                string elemVal = EmitExpression(sb: sb, expr: arg);
                TypeInfo? elemType = GetExpressionType(expr: arg);
                string elemLlvm = elemType != null ? GetLlvmType(type: elemType) : "i64";

                if (!_generatedRoutines.Contains(item: mangledAdd))
                {
                    string retType = baseName is "Set" or "SortedSet" ? "i1" : "void";
                    _rfRoutineDeclarations[key: mangledAdd] =
                        $"declare {retType} @{mangledAdd}(ptr, {elemLlvm})";
                    _generatedRoutines.Add(item: mangledAdd);
                }

                bool returnsVoid = resolvedAdd.Routine.ReturnType == null ||
                                   resolvedAdd.Routine.ReturnType.Name == "Blank";
                if (returnsVoid)
                {
                    EmitLine(sb: sb,
                        line: $"  call void @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
                }
                else
                {
                    string discarded = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {discarded} = call i1 @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
                }
            }
        }

        return collectionPtr;
    }

    /// <summary>
    /// Runtime fallback for BitArray construction when arguments are non-literal booleans.
    /// </summary>
    private string EmitBitArrayRuntime(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string llvmType = GetLlvmType(type: resolvedType);
        int bitCount = arguments.Count;
        int byteCount = (bitCount + 7) / 8;

        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            string byteAccum = "0";
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                string boolVal =
                    EmitExpression(sb: sb, expr: arguments[index: byteIdx * 8 + bitIdx]);
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
    /// Emits a zero-arg $create() call for a collection type, handling monomorphization.
    /// </summary>
    private string EmitCollectionCreate(StringBuilder sb, TypeInfo? resolvedType, string typeName)
    {
        if (resolvedType == null) return "null";

        ResolvedMemberRoutine? resolved = resolvedType.IsGenericResolution
            ? null
            : ResolveMemberRoutine(receiverType: resolvedType, methodName: "$create");

        if (resolved != null && resolved.Routine.Parameters.Count > 0)
            resolved = null;

        if (resolved == null)
        {
            string createName = $"{resolvedType.FullName}.$create";
            RoutineInfo? creator =
                _registry.LookupRoutineOverload(baseName: createName, argTypes: new List<TypeInfo>());
            if (creator != null && creator.Parameters.Count > 0)
                creator = null;

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
                    string genCreateName = $"{RoutineInfo.GetTypeIdentity(type: genericDef)}.$create";
                    creator = _registry.LookupRoutineOverload(baseName: genCreateName,
                        argTypes: new List<TypeInfo>());
                    creator ??= _registry.LookupRoutine(fullName: genCreateName);
                    if (creator != null && creator.Parameters.Count > 0)
                        creator = null;
                }
            }

            if (creator != null)
            {
                string funcName;
                if (resolvedType.IsGenericResolution)
                    funcName = Q(name: $"{resolvedType.FullName}.$create");
                else
                    funcName = MangleRoutineName(routine: creator);

                if (!_generatedRoutines.Contains(item: funcName))
                    GenerateRoutineDeclaration(routine: creator, nameOverride: funcName);

                string result = NextTemp();
                EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
                return result;
            }
        }
        else
        {
            string funcName = resolved.MangledName;
            if (!_generatedRoutines.Contains(item: funcName))
                GenerateRoutineDeclaration(routine: resolved.Routine, nameOverride: funcName);

            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
            return result;
        }

        throw new InvalidOperationException(
            $"No '$create' routine found for collection type '{resolvedType.Name}'. " +
            "All collection types must have a registered '$create' body in the stdlib.");
    }
}
