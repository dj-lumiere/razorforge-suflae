namespace Compilers.Analysis.Symbols;

using Compilers.Shared.AST;
using TypeSymbol = Compilers.Analysis.Types.TypeInfo;

/// <summary>
/// Information about a field in a record, entity, or resident.
/// </summary>
public sealed class FieldInfo
{
    /// <summary>The name of the field.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the field.</summary>
    public TypeSymbol Type { get; }

    /// <summary>Whether this field is mutable (var) or immutable (let).</summary>
    public bool IsMutable { get; init; }

    /// <summary>Visibility for reading (getter).</summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Public;

    /// <summary>Visibility for writing (setter), if different from getter.</summary>
    public VisibilityModifier? SetterVisibility { get; init; }

    /// <summary>The effective setter visibility (uses getter visibility if not specified).</summary>
    public VisibilityModifier EffectiveSetterVisibility =>
        SetterVisibility ?? Visibility;

    /// <summary>The index of this field within the containing type.</summary>
    public int Index { get; init; }

    /// <summary>Whether this field has a default value.</summary>
    public bool HasDefaultValue { get; init; }

    /// <summary>Source location where this field is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The type that owns this field.</summary>
    public TypeSymbol? Owner { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FieldInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <param name="type">The resolved type of the field.</param>
    public FieldInfo(string name, TypeSymbol type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// Creates a copy of this field with the type substituted for generic instantiation.
    /// </summary>
    /// <param name="newType">The new type to substitute.</param>
    /// <returns>A new <see cref="FieldInfo"/> with the substituted type.</returns>
    public FieldInfo WithSubstitutedType(TypeSymbol newType)
    {
        return new FieldInfo(name: Name, type: newType)
        {
            IsMutable = IsMutable,
            Visibility = Visibility,
            SetterVisibility = SetterVisibility,
            Index = Index,
            HasDefaultValue = HasDefaultValue,
            Location = Location,
            Owner = Owner
        };
    }
}
