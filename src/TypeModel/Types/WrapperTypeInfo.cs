namespace TypeModel.Types;

using TypeModel.Enums;

/// <summary>
/// Builder-synthesized wrapper types (Viewed, Grasped, Retained, Tracked, Shared, Marked, Inspected, Claimed, Hijacked, Owned).
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
    /// <param name="wrapperName">The name of the wrapper type (e.g., "Grasped", "Viewed").</param>
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
            isReadOnly: IsReadOnly) { Module = Module };
    }

    /// <summary>
    /// Well-known wrapper type definitions.
    /// These are used as templates for creating resolved wrapper types.
    /// </summary>
    public static class WellKnown
    {
        /// <summary>
        /// Read-only single-threaded wrapper. Provides unmodifiable view of the inner value.
        /// </summary>
        public static readonly WrapperTypeInfo ViewedDefinition = new(wrapperName: "Viewed",
            innerType: ErrorTypeInfo.Instance, // Placeholder, will be resolved with actual type
            isReadOnly: true) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-write single-threaded wrapper. Provides modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeInfo GraspedDefinition = new(
            wrapperName: "Grasped",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Read-only multi-threaded wrapper. Thread-safe unmodifiable view.
        /// </summary>
        public static readonly WrapperTypeInfo InspectedDefinition = new(
            wrapperName: "Inspected",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: true) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-write multi-threaded wrapper. Thread-safe modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeInfo ClaimedDefinition = new(
            wrapperName: "Claimed",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Reference-counted single-threaded handle. Shared ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeInfo RetainedDefinition = new(
            wrapperName: "Retained",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Weak single-threaded handle. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeInfo TrackedWeakDefinition = new(
            wrapperName: "Tracked",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Reference-counted wrapper. Shared ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeInfo SharedDefinition = new(
            wrapperName: "Shared",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Weak-reference wrapper. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeInfo TrackedDefinition = new(
            wrapperName: "Marked",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Unsafe raw-pointer wrapper. Danger zone only.
        /// </summary>
        public static readonly WrapperTypeInfo HijackedDefinition = new(
            wrapperName: "Hijacked",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-ownership wrapper. Equivalent to unique_ptr — single owner, freed at scope end.
        /// Constraint: T must be EntityType. Transfer via steal.
        /// </summary>
        public static readonly WrapperTypeInfo OwnedDefinition = new(
            wrapperName: "Owned",
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>All well-known wrapper type definitions.</summary>
        public static IEnumerable<WrapperTypeInfo> All =>
        [
            ViewedDefinition,
            GraspedDefinition,
            RetainedDefinition,
            TrackedWeakDefinition,
            InspectedDefinition,
            ClaimedDefinition,
            SharedDefinition,
            TrackedDefinition,
            HijackedDefinition,
            OwnedDefinition
        ];
    }
}
