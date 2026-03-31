namespace SemanticAnalysis.Types;

using Enums;

/// <summary>
/// Builder-synthesized wrapper types (Hijacked, Inspected, Seized, Viewed, Shared, Tracked, Snatched).
/// These types transparently forward member access to their inner type while providing
/// ownership and access control semantics.
/// </summary>
public sealed class WrapperTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Wrapper;

    /// <summary>The inner type being wrapped (T in Wrapper&lt;T&gt;).</summary>
    public TypeInfo InnerType { get; }

    /// <summary>Whether this is a read-only wrapper (Viewed, Inspected).</summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="WrapperTypeInfo"/> class.
    /// </summary>
    /// <param name="wrapperName">The name of the wrapper type (e.g., "Hijacked", "Viewed").</param>
    /// <param name="innerType">The type being wrapped.</param>
    /// <param name="isReadOnly">Whether this is a read-only wrapper.</param>
    public WrapperTypeInfo(string wrapperName, TypeInfo innerType, bool isReadOnly = false) : base(
        name: wrapperName)
    {
        InnerType = innerType;
        IsReadOnly = isReadOnly;
        TypeArguments = [innerType];
    }

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        if (typeArguments.Count != 1)
        {
            throw new InvalidOperationException(
                message: $"Wrapper type '{Name}' requires exactly one type argument.");
        }

        return new WrapperTypeInfo(wrapperName: Name,
            innerType: typeArguments[index: 0],
            isReadOnly: IsReadOnly);
    }

    /// <summary>
    /// Well-known wrapper type definitions.
    /// These are used as templates for creating resolved wrapper types.
    /// </summary>
    public static class WellKnown
    {
        /// <summary>
        /// Read-only single-threaded token. Provides unmodifiable view of the inner value.
        /// </summary>
        public static readonly WrapperTypeInfo ViewedDefinition = new(wrapperName: "Viewed",
            innerType: ErrorTypeInfo.Instance, // Placeholder, will be resolved with actual type
            isReadOnly: true) { GenericParameters = ["T"] };

        /// <summary>
        /// Exclusive write single-threaded token. Provides modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeInfo HijackedDefinition = new(
            wrapperName: "Hijacked",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"] };

        /// <summary>
        /// Read-only multi-threaded token. Thread-safe unmodifiable view.
        /// </summary>
        public static readonly WrapperTypeInfo InspectedDefinition = new(
            wrapperName: "Inspected",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: true) { GenericParameters = ["T"] };

        /// <summary>
        /// Exclusive write multi-threaded token. Thread-safe modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeInfo SeizedDefinition = new(
            wrapperName: "Seized",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"] };

        /// <summary>
        /// Reference-counted handle. Shared ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeInfo SharedDefinition = new(
            wrapperName: "Shared",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"] };

        /// <summary>
        /// Weak reference handle. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeInfo TrackedDefinition = new(
            wrapperName: "Tracked",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"] };

        /// <summary>
        /// Unsafe raw pointer handle. Danger zone only.
        /// </summary>
        public static readonly WrapperTypeInfo SnatchedDefinition = new(
            wrapperName: "Snatched",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"] };

        /// <summary>All well-known wrapper type definitions.</summary>
        public static IEnumerable<WrapperTypeInfo> All =>
        [
            ViewedDefinition,
            HijackedDefinition,
            InspectedDefinition,
            SeizedDefinition,
            SharedDefinition,
            TrackedDefinition,
            SnatchedDefinition
        ];
    }
}
