namespace TypeModel.Types;

using TypeModel.Enums;
using TypeModel.Symbols;

/// <summary>
/// Type information for records (value types with copy semantics).
/// Includes "primitive-like" types (s32, bool, etc.) which are single-member-variable records
/// wrapping LLVM intrinsics.
/// </summary>
public sealed class RecordTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Record;

    /// <summary>MemberVariables declared in this record.</summary>
    public IReadOnlyList<MemberVariableInfo> MemberVariables { get; set; } = [];

    /// <summary>Protocols this record implements (obeys).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; set; } = [];

    /// <summary>
    /// Backend type from @llvm("type") annotation. Null if not a backend-annotated type.
    /// </summary>
    public string? BackendType { get; init; }

    /// <summary>
    /// Whether this record has a direct backend type mapping (via @llvm annotation).
    /// </summary>
    public bool HasDirectBackendType => BackendType != null;

    /// <summary>
    /// Whether this is a single-member-variable record that wraps an intrinsic type.
    /// These records can be treated as their underlying LLVM type for operations.
    /// Examples: s32, bool, f64, Address
    /// </summary>
    public bool IsSingleMemberVariableWrapper => MemberVariables is [{ Type: IntrinsicTypeInfo }];

    /// <summary>
    /// For single-member-variable wrappers, gets the underlying intrinsic type.
    /// Returns null if not a single-member-variable wrapper.
    /// </summary>
    public IntrinsicTypeInfo? UnderlyingIntrinsic =>
        IsSingleMemberVariableWrapper
            ? MemberVariables[index: 0].Type as IntrinsicTypeInfo
            : null;

    /// <summary>
    /// The LLVM type representation for this record.
    /// For @llvm-annotated records, uses the backend type directly.
    /// For single-member-variable wrappers, this is the intrinsic type (e.g., "i32").
    /// For multi-member-variable records, this is a struct type.
    /// </summary>
    public string LlvmType
    {
        get
        {
            if (BackendType != null)
            {
                return BackendType;
            }

            if (UnderlyingIntrinsic != null)
            {
                return UnderlyingIntrinsic.LlvmType;
            }

            // Multi-member-variable record: struct type
            string memberVariableTypes = string.Join(separator: ", ",
                values: MemberVariables.Select(selector: GetLlvmTypeForMemberVariable));
            return $"{{ {memberVariableTypes} }}";
        }
    }

    /// <summary>RC wrapper base names that need retain-on-copy / release-on-drop.</summary>
    private static readonly HashSet<string> RCWrapperBaseNames =
        ["Retained", "Shared", "Tracked", "Marked"];

    /// <summary>Whether this record has RC wrapper fields needing retain-on-copy / release-on-drop.</summary>
    public bool HasRCFields => MemberVariables.Any(predicate: f =>
        f.Type is WrapperTypeInfo w && RCWrapperBaseNames.Contains(item: w.Name));

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public RecordTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Looks up a member variable by name in this record.
    /// </summary>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    public MemberVariableInfo? LookupMemberVariable(string memberVariableName)
    {
        return MemberVariables.FirstOrDefault(predicate: f => f.Name == memberVariableName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the record type.</param>
    public RecordTypeInfo(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Record '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Substitute types in member variables
        var substitutedMemberVariables = MemberVariables.Select(selector: f =>
                                                             SubstituteMemberVariableType(
                                                                 memberVariable: f,
                                                                 substitution: substitution))
                                                        .ToList();

        // Build resolved type name using FullName for each type argument so the resolved
        // type carries fully-qualified inner names (e.g., "Hijacked[Core.Byte]").
        // TypeInfo.FullName then prepends the module: "Core.Hijacked[Core.Byte]".
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.FullName))}]";

        var substitutedProtocols = ImplementedProtocols
            .Select(selector: p => (ProtocolTypeInfo)SubstituteType(type: p, substitution: substitution))
            .ToList();

        return new RecordTypeInfo(name: resolvedName)
        {
            MemberVariables = substitutedMemberVariables,
            ImplementedProtocols = substitutedProtocols,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            BackendType =
                ResolveBackendTypeTemplate(template: BackendType,
                    genericParams: GenericParameters,
                    typeArguments: typeArguments),
            Visibility = Visibility,
            Location = Location,
            Module = Module
        };
    }

    /// <summary>
    /// Resolves template holes in a BackendType string during generic instantiation.
    /// Template holes: {N} for const generic values, {T} for type LLVM types,
    /// {(N+7)//8} for arithmetic expressions over const generics.
    /// Returns the template unchanged if it contains no holes.
    /// </summary>
    private static string? ResolveBackendTypeTemplate(string? template,
        IReadOnlyList<string>? genericParams, IReadOnlyList<TypeInfo> typeArguments)
    {
        if (template == null || genericParams == null || !template.Contains(value: '{'))
        {
            return template;
        }

        var paramMap = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < genericParams.Count && i < typeArguments.Count; i++)
        {
            paramMap[key: genericParams[index: i]] = typeArguments[index: i];
        }

        var result = new System.Text.StringBuilder();
        int pos = 0;
        while (pos < template.Length)
        {
            int open = template.IndexOf(value: '{', startIndex: pos);
            if (open < 0)
            {
                result.Append(value: template, startIndex: pos, count: template.Length - pos);
                break;
            }

            result.Append(value: template, startIndex: pos, count: open - pos);
            int close = template.IndexOf(value: '}', startIndex: open + 1);
            if (close < 0)
            {
                result.Append(value: template, startIndex: open, count: template.Length - open);
                break;
            }

            string hole = template[(open + 1)..close]
               .Trim();
            result.Append(value: ResolveHole(hole: hole, paramMap: paramMap));
            pos = close + 1;
        }

        return result.ToString();
    }

    private static string ResolveHole(string hole, Dictionary<string, TypeInfo> paramMap)
    {
        // Simple parameter name: {N} or {T}
        if (paramMap.TryGetValue(key: hole, value: out TypeInfo? typeArg))
        {
            return SubstituteTypeArg(typeArg: typeArg);
        }

        // Arithmetic expression: {(N+7)//8}
        var constValues = new Dictionary<string, long>();
        foreach ((string name, TypeInfo ti) in paramMap)
        {
            if (ti is ConstGenericValueTypeInfo constVal)
            {
                constValues[key: name] = constVal.Value;
            }
        }

        if (constValues.Count > 0)
        {
            return EvaluateConstExpr(expr: hole, paramValues: constValues)
               .ToString();
        }

        return hole; // fallback: return as-is
    }

    private static string SubstituteTypeArg(TypeInfo typeArg)
    {
        if (typeArg is ConstGenericValueTypeInfo constVal)
        {
            return constVal.Value.ToString();
        }

        if (typeArg is RecordTypeInfo record)
        {
            return record.LlvmType;
        }

        return "ptr"; // entities, protocols, etc. are pointers
    }

    /// <summary>
    /// Evaluates a simple arithmetic expression with const generic parameter values.
    /// Supports: integer literals, parameter references, +, -, *, // (integer division), parentheses.
    /// </summary>
    private static long EvaluateConstExpr(string expr, Dictionary<string, long> paramValues)
    {
        int pos = 0;
        long result = ParseAddSub(expr: expr, pos: ref pos, paramValues: paramValues);
        return result;
    }

    private static long ParseAddSub(string expr, ref int pos, Dictionary<string, long> paramValues)
    {
        long left = ParseMulDiv(expr: expr, pos: ref pos, paramValues: paramValues);
        while (pos < expr.Length)
        {
            SkipWhitespace(expr: expr, pos: ref pos);
            if (pos < expr.Length && expr[index: pos] == '+')
            {
                pos++;
                left += ParseMulDiv(expr: expr, pos: ref pos, paramValues: paramValues);
            }
            else if (pos < expr.Length && expr[index: pos] == '-')
            {
                pos++;
                left -= ParseMulDiv(expr: expr, pos: ref pos, paramValues: paramValues);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    private static long ParseMulDiv(string expr, ref int pos, Dictionary<string, long> paramValues)
    {
        long left = ParseAtom(expr: expr, pos: ref pos, paramValues: paramValues);
        while (pos < expr.Length)
        {
            SkipWhitespace(expr: expr, pos: ref pos);
            if (pos + 1 < expr.Length && expr[index: pos] == '/' && expr[index: pos + 1] == '/')
            {
                pos += 2;
                left /= ParseAtom(expr: expr, pos: ref pos, paramValues: paramValues);
            }
            else if (pos < expr.Length && expr[index: pos] == '*')
            {
                pos++;
                left *= ParseAtom(expr: expr, pos: ref pos, paramValues: paramValues);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    private static long ParseAtom(string expr, ref int pos, Dictionary<string, long> paramValues)
    {
        SkipWhitespace(expr: expr, pos: ref pos);
        if (pos < expr.Length && expr[index: pos] == '(')
        {
            pos++;
            long val = ParseAddSub(expr: expr, pos: ref pos, paramValues: paramValues);
            SkipWhitespace(expr: expr, pos: ref pos);
            if (pos < expr.Length && expr[index: pos] == ')')
            {
                pos++;
            }

            return val;
        }

        if (pos < expr.Length && char.IsDigit(c: expr[index: pos]))
        {
            int start = pos;
            while (pos < expr.Length && char.IsDigit(c: expr[index: pos]))
            {
                pos++;
            }

            return long.Parse(s: expr[start..pos]);
        }

        if (pos < expr.Length && char.IsLetter(c: expr[index: pos]))
        {
            int start = pos;
            while (pos < expr.Length &&
                   (char.IsLetterOrDigit(c: expr[index: pos]) || expr[index: pos] == '_'))
            {
                pos++;
            }

            string name = expr[start..pos];
            if (paramValues.TryGetValue(key: name, value: out long val))
            {
                return val;
            }

            throw new InvalidOperationException(
                message: $"Unknown parameter '{name}' in @llvm template expression");
        }

        throw new InvalidOperationException(
            message:
            $"Unexpected character in @llvm template expression at position {pos}: '{expr}'");
    }

    private static void SkipWhitespace(string expr, ref int pos)
    {
        while (pos < expr.Length && char.IsWhiteSpace(c: expr[index: pos]))
        {
            pos++;
        }
    }

    /// <summary>
    /// Substitutes the type in a member variable for generic resolution.
    /// </summary>
    /// <param name="memberVariable">The member variable to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="MemberVariableInfo"/> with the substituted type.</returns>
    private static MemberVariableInfo SubstituteMemberVariableType(
        MemberVariableInfo memberVariable, Dictionary<string, TypeInfo> substitution)
    {
        TypeInfo substitutedType =
            SubstituteType(type: memberVariable.Type, substitution: substitution);
        return memberVariable.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    /// <param name="type">The type to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>The substituted type, or the original if no substitution applies.</returns>
    internal static TypeInfo SubstituteType(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        // If it's a type parameter, substitute it
        if (substitution.TryGetValue(key: type.Name, value: out TypeInfo? substituted))
        {
            return substituted;
        }

        // If it's a generic resolution, recursively substitute
        if (!type.IsGenericResolution || type.TypeArguments == null)
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg =>
                               SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        // Route through the ambient TypeRegistry so entity-type specializations
        // (e.g. Maybe[Text] → { Hijacked[T] } layout) are picked up.
        Compiler.Resolution.TypeRegistry? registry = Compiler.Resolution.TypeRegistry.Ambient;

        // Get the generic definition and create resolved instance with new args
        if (type is RecordTypeInfo { GenericDefinition: not null } recordType)
        {
            return registry != null
                ? registry.GetOrCreateResolution(genericDef: recordType.GenericDefinition, typeArguments: newArgs)
                : recordType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is EntityTypeInfo { GenericDefinition: not null } entityType)
        {
            return registry != null
                ? registry.GetOrCreateResolution(genericDef: entityType.GenericDefinition, typeArguments: newArgs)
                : entityType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is ProtocolTypeInfo { GenericDefinition: not null } protocolType)
        {
            return registry != null
                ? registry.GetOrCreateResolution(genericDef: protocolType.GenericDefinition, typeArguments: newArgs)
                : protocolType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is WrapperTypeInfo wrapperType)
        {
            return wrapperType.CreateInstance(typeArguments: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Gets the LLVM type string for a member variable.
    /// </summary>
    /// <param name="memberVariable">The member variable to get the LLVM type for.</param>
    /// <returns>The LLVM type string.</returns>
    private static string GetLlvmTypeForMemberVariable(MemberVariableInfo memberVariable)
    {
        return memberVariable.Type switch
        {
            IntrinsicTypeInfo intrinsic => intrinsic.LlvmType,
            RecordTypeInfo record => record.LlvmType,
            _ => "ptr" // Reference types are pointers
        };
    }
}
