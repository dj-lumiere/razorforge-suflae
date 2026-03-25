namespace SemanticAnalysis.Types;

using Enums;

/// <summary>
/// Type information for choices (simple enumerations with optional integer values).
/// Choices CAN have methods, unlike variants.
/// Cases use SCREAMING_SNAKE_CASE and numbering is all-or-nothing
/// (either all have explicit values or none).
/// </summary>
public sealed class ChoiceTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Choice;

    /// <summary>The cases (variants) of this choice.</summary>
    public IReadOnlyList<ChoiceCaseInfo> Cases { get; init; } = [];

    /// <summary>Protocols this choice implements (obeys).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; init; } = [];

    /// <summary>
    /// Whether all cases have explicit values.
    /// Numbering must be all-or-nothing.
    /// </summary>
    public bool HasExplicitValues => Cases.All(predicate: c => c.Value.HasValue);

    /// <summary>
    /// The underlying integer type for this choice.
    /// Defaults to s32 but can be specified.
    /// </summary>
    public TypeInfo? UnderlyingType { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChoiceTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the choice type.</param>
    public ChoiceTypeInfo(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as choice types cannot be generic.</exception>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        // Choices are not generic
        throw new InvalidOperationException(
            message: $"Choice type '{Name}' cannot be resolved with type arguments.");
    }
}
