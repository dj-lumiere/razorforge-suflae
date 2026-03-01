namespace SemanticAnalysis.Symbols;

using SyntaxTree;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Information about a field in a record, entity, or resident.
/// </summary>
/// <remarks>
/// Field visibility uses the four-level system:
/// <list type="bullet">
/// <item>public - read/write from anywhere</item>
/// <item>published - public read, private write</item>
/// <item>internal - read/write within module</item>
/// <item>private - read/write within file</item>
/// </list>
/// </remarks>
public sealed class FieldInfo
{
    /// <summary>The name of the field.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the field.</summary>
    public TypeSymbol Type { get; }

    /// <summary>
    /// Visibility for the field (open, posted, secret).
    /// For posted fields, read is open but write is secret.
    /// </summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Open;

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
    /// Creates a copy of this field with the type substituted for generic resolution.
    /// </summary>
    /// <param name="newType">The new type to substitute.</param>
    /// <returns>A new <see cref="FieldInfo"/> with the substituted type.</returns>
    public FieldInfo WithSubstitutedType(TypeSymbol newType)
    {
        return new FieldInfo(name: Name, type: newType)
        {
            Visibility = Visibility,
            Index = Index,
            HasDefaultValue = HasDefaultValue,
            Location = Location,
            Owner = Owner
        };
    }
}