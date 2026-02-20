namespace Compilers.Analysis.Types;

using Enums;
using Symbols;

/// <summary>
/// The kind of tuple: ValueTuple (record-like), FixedTuple (resident-like), or Tuple (entity-like).
/// </summary>
public enum TupleKind
{
    /// <summary>Value type (like record). Copy semantics. Only contains value types.</summary>
    Value,

    /// <summary>Resident type (like resident). Reference semantics, fixed size. Contains records + residents.</summary>
    Fixed,

    /// <summary>Reference type (like entity). Reference semantics, heap-allocated. Contains anything.</summary>
    Reference
}

/// <summary>
/// Type information for compiler-generated tuple types.
/// Tuples are variadic and can contain any number of elements.
///
/// - ValueTuple: All elements are value types (copy semantics)
/// - FixedTuple: All elements are resident-compatible (resident semantics, fixed size)
/// - Tuple: Any element is a reference type (reference semantics, heap-allocated)
/// </summary>
public sealed class TupleTypeInfo : TypeInfo
{
    /// <summary>
    /// The category of this type.
    /// </summary>
    public override TypeCategory Category => TypeCategory.Tuple;

    /// <summary>
    /// The kind of this tuple (Value, Fixed, or Reference).
    /// </summary>
    public TupleKind Kind { get; }

    /// <summary>
    /// Whether this is a ValueTuple (all elements are value types).
    /// </summary>
    public bool IsValueTuple => Kind == TupleKind.Value;

    /// <summary>
    /// Whether this is a FixedTuple (resident-like, all elements are resident-compatible).
    /// </summary>
    public bool IsFixedTuple => Kind == TupleKind.Fixed;

    /// <summary>
    /// The element types in order (item0, item1, ..., itemN).
    /// </summary>
    public IReadOnlyList<TypeInfo> ElementTypes { get; }

    /// <summary>
    /// Synthetic fields generated for this tuple (item0, item1, etc.).
    /// </summary>
    public IReadOnlyList<FieldInfo> Fields { get; }

    /// <summary>
    /// Creates a new tuple type with the specified element types and kind.
    /// </summary>
    /// <param name="elementTypes">The types of each element in the tuple.</param>
    /// <param name="kind">The kind of tuple (Value, Fixed, or Reference).</param>
    public TupleTypeInfo(IReadOnlyList<TypeInfo> elementTypes, TupleKind kind)
        : base(name: kind switch
        {
            TupleKind.Value => "ValueTuple",
            TupleKind.Fixed => "FixedTuple",
            _ => "Tuple"
        })
    {
        ElementTypes = elementTypes;
        Kind = kind;

        // Generate synthetic fields: item0, item1, ..., itemN
        var fields = new List<FieldInfo>(capacity: elementTypes.Count);
        for (int i = 0; i < elementTypes.Count; i++)
        {
            fields.Add(item: new FieldInfo(name: $"item{i}", type: elementTypes[i])
            {
                IsMutable = false, // Tuple fields are immutable
                Visibility = VisibilityModifier.Open,
                Index = i
            });
        }

        Fields = fields;

        // Set TypeArguments so FullName displays correctly (e.g., "ValueTuple<S32, S32>")
        TypeArguments = elementTypes;
    }

    /// <summary>
    /// Creates a new tuple type with the specified element types.
    /// Backward-compatible constructor: maps true → Value, false → Reference.
    /// </summary>
    /// <param name="elementTypes">The types of each element in the tuple.</param>
    /// <param name="isValueTuple">True if all elements are value types.</param>
    public TupleTypeInfo(IReadOnlyList<TypeInfo> elementTypes, bool isValueTuple)
        : this(elementTypes: elementTypes, kind: isValueTuple ? TupleKind.Value : TupleKind.Reference)
    {
    }

    /// <summary>
    /// Tuples don't support further instantiation - they are already concrete types.
    /// </summary>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: "Tuple types cannot be further instantiated. Create a new TupleTypeInfo instead.");
    }

    /// <summary>
    /// Gets the field info for a specific element index.
    /// </summary>
    /// <param name="index">The zero-based index (0 for item0, 1 for item1, etc.).</param>
    /// <returns>The field info, or null if index is out of range.</returns>
    public FieldInfo? GetField(int index)
    {
        return index >= 0 && index < Fields.Count ? Fields[index] : null;
    }

    /// <summary>
    /// Gets the field info by name (item0, item1, etc.).
    /// </summary>
    /// <param name="fieldName">The field name (e.g., "item0", "item1").</param>
    /// <returns>The field info, or null if not found.</returns>
    public FieldInfo? GetField(string fieldName)
    {
        if (!fieldName.StartsWith(value: "item", comparisonType: StringComparison.Ordinal))
        {
            return null;
        }

        if (int.TryParse(s: fieldName.AsSpan(start: 4), result: out int index))
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
