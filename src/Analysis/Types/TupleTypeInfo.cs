namespace SemanticAnalysis.Types;

using Enums;
using Symbols;

/// <summary>
/// Type information for builder-generated tuple types.
/// Tuples are always inline LLVM structs (value semantics). Entities stored as ptr fields.
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
    /// Creates a new tuple type with the specified element types.
    /// </summary>
    /// <param name="elementTypes">The types of each element in the tuple.</param>
    public TupleTypeInfo(IReadOnlyList<TypeInfo> elementTypes)
        : base(name: "Tuple")
    {
        ElementTypes = elementTypes;

        // Generate synthetic member variables: item0, item1, ..., itemN
        var memberVariables = new List<MemberVariableInfo>(capacity: elementTypes.Count);
        for (int i = 0; i < elementTypes.Count; i++)
        {
            memberVariables.Add(item: new MemberVariableInfo(name: $"item{i}", type: elementTypes[i])
            {
                Visibility = VisibilityModifier.Open,
                Index = i
            });
        }

        MemberVariables = memberVariables;

        // Set TypeArguments so FullName displays correctly (e.g., "Tuple<S32, S32>")
        TypeArguments = elementTypes;
    }

    /// <summary>
    /// Tuples don't support further resolution - they are already concrete types.
    /// </summary>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: "Tuple types cannot be further resolved. Create a new TupleTypeInfo instead.");
    }

    /// <summary>
    /// Gets the member variable info for a specific element index.
    /// </summary>
    /// <param name="index">The zero-based index (0 for item0, 1 for item1, etc.).</param>
    /// <returns>The member variable info, or null if index is out of range.</returns>
    public MemberVariableInfo? GetField(int index)
    {
        return index >= 0 && index < MemberVariables.Count ? MemberVariables[index] : null;
    }

    /// <summary>
    /// Gets the member variable info by name (item0, item1, etc.).
    /// </summary>
    /// <param name="memberVariableName">The member variable name (e.g., "item0", "item1").</param>
    /// <returns>The member variable info, or null if not found.</returns>
    public MemberVariableInfo? GetField(string memberVariableName)
    {
        if (!memberVariableName.StartsWith(value: "item", comparisonType: StringComparison.Ordinal))
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
