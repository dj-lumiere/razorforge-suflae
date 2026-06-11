using System;
using System.Collections.Generic;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Represents a generic type parameter (like T in <c>List[T]</c>).
/// This is an unbound placeholder that gets replaced during generic resolution.
/// </summary>
public sealed class GenericParameterTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.TypeParameter;

    /// <summary>
    /// When this param was produced by <c>WrapperForwardingPass</c>'s inner-T-rename
    /// (an inner type's generic param name collides with the wrapper's own param name),
    /// holds the original inner-param name (the substitution lookup key in monomorphization).
    /// Null for normal (non-renamed) generic params.
    /// </summary>
    /// <remarks>
    /// Substitution sites should look up this property first (when non-null) before falling
    /// back to <see cref="TypeInfo.Name"/>. This replaces the older string-prefix sentinel
    /// (e.g. <c>__rfwd_T__</c>) with a structural marker that doesn't require lexical
    /// awareness in every substitution path.
    /// </remarks>
    public string? ForwarderOriginalName { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericParameterTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the generic type parameter.</param>
    public GenericParameterTypeInfo(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as generic parameters cannot be resolved.</exception>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(message: "Cannot resolve a generic type parameter.");
    }
}
