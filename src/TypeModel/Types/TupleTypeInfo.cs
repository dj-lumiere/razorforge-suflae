namespace TypeModel.Types;

using TypeModel.Enums;
using TypeModel.Symbols;

/// <summary>
/// Type information for tuple types.
/// Both <c>ValueTuple</c> and <c>Tuple</c> are inline LLVM structs (value semantics, never
/// heap-allocated). The distinction affects copy/drop semantics:
/// <list type="bullet">
///   <item><c>ValueTuple</c> — all elements are record-like (records, choices, flags, variants,
///         nested ValueTuples) → trivial copy (memcpy), no RC operations.</item>
///   <item><c>Tuple</c> — at least one element is entity-like (entity, crashable, nested Tuple)
///         → copy bumps RC of entity elements; drop decrements them. Entity elements are stored
///         as raw pointers; the programmer is responsible for ensuring the entities outlive the
///         tuple (no automatic <c>Retained[T]</c> wrapping).</item>
/// </list>
/// </summary>
public sealed class TupleTypeInfo : TypeInfo
{
    /// <summary>
    /// The category of this type.
    /// </summary>
    public override TypeCategory Category => TypeCategory.Tuple;

    /// <summary>
    /// The element types in order (item0, item1, ..., itemN).
    /// </summary>
    public IReadOnlyList<TypeInfo> ElementTypes { get; }

    /// <summary>
    /// Synthetic member variables generated for this tuple (item0, item1, etc.).
    /// </summary>
    public IReadOnlyList<MemberVariableInfo> MemberVariables { get; }

    /// <summary>
    /// <c>true</c> when all elements are record-like (records, choices, flags, variants,
    /// nested <c>ValueTuple</c>s) — copy is a plain memcpy with no RC operations.<br/>
    /// <c>false</c> when at least one element is entity-like — the tuple is still inline
    /// but copy/drop must manage RC for entity elements.
    /// </summary>
    public bool IsValueTuple { get; }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="t"/> is record-like for tuple inference purposes:
    /// records, choices, flags, variants, and nested ValueTuples all qualify.
    /// Entities, crashables, and nested Tuples (which contain entities) do not.
    /// </summary>
    public static bool IsRecordLike(TypeInfo t) => t switch
    {
        RecordTypeInfo  => true,
        ChoiceTypeInfo  => true,
        FlagsTypeInfo   => true,
        VariantTypeInfo => true,
        TupleTypeInfo tt => tt.IsValueTuple,
        _ => false
    };

    /// <summary>
    /// Creates a new tuple type with the specified element types.
    /// </summary>
    /// <param name="elementTypes">The types of each element in the tuple.</param>
    public TupleTypeInfo(IReadOnlyList<TypeInfo> elementTypes) : base(
        name: BuildName(elementTypes: elementTypes))
    {
        ElementTypes = elementTypes;
        IsValueTuple  = elementTypes.All(predicate: IsRecordLike);

        // Generate synthetic member variables: item0, item1, ..., itemN
        var memberVariables = new List<MemberVariableInfo>(capacity: elementTypes.Count);
        for (int i = 0; i < elementTypes.Count; i++)
        {
            memberVariables.Add(
                item: new MemberVariableInfo(name: $"item{i}", type: elementTypes[index: i])
                {
                    Visibility = VisibilityModifier.Open, Index = i
                });
        }

        MemberVariables = memberVariables;

        TypeArguments = elementTypes;
    }

    private static string BuildName(IReadOnlyList<TypeInfo> elementTypes)
    {
        bool isValue = elementTypes.All(predicate: IsRecordLike);
        string prefix = isValue ? "ValueTuple" : "Tuple";
        string args   = string.Join(separator: ", ",
            values: elementTypes.Select(selector: t => t.Name));
        return $"{prefix}[{args}]";
    }

    /// <summary>
    /// Tuples don't support further resolution - they are already concrete types.
    /// </summary>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message:
            "Tuple types cannot be further resolved. Create a new TupleTypeInfo instead.");
    }

    /// <summary>
    /// Gets the member variable info for a specific element index.
    /// </summary>
    /// <param name="index">The zero-based index (0 for item0, 1 for item1, etc.).</param>
    /// <returns>The member variable info, or null if index is out of range.</returns>
    public MemberVariableInfo? GetField(int index)
    {
        return index >= 0 && index < MemberVariables.Count
            ? MemberVariables[index: index]
            : null;
    }

    /// <summary>
    /// Gets the member variable info by name (item0, item1, etc.).
    /// </summary>
    /// <param name="memberVariableName">The member variable name (e.g., "item0", "item1").</param>
    /// <returns>The member variable info, or null if not found.</returns>
    public MemberVariableInfo? GetField(string memberVariableName)
    {
        if (!memberVariableName.StartsWith(value: "item",
                comparisonType: StringComparison.Ordinal))
        {
            return null;
        }

        if (int.TryParse(s: memberVariableName.AsSpan(start: 4), result: out int index))
        {
            return GetField(index: index);
        }

        return null;
    }

    /// <summary>
    /// The number of elements in this tuple.
    /// </summary>
    public int Arity => ElementTypes.Count;
}
