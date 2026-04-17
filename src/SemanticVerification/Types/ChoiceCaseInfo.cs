namespace SemanticVerification.Types;

using SyntaxTree;

/// <summary>
/// Information about a single case in a choice type.
/// </summary>
public sealed class ChoiceCaseInfo
{
    /// <summary>The name of the case (SCREAMING_SNAKE_CASE).</summary>
    public string Name { get; }

    /// <summary>The explicit integer value, if specified.</summary>
    public int? Value { get; init; }

    /// <summary>The computed integer value (either explicit or auto-assigned).</summary>
    public int ComputedValue { get; init; }

    /// <summary>Source location where this case is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChoiceCaseInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the choice case.</param>
    public ChoiceCaseInfo(string name)
    {
        Name = name;
    }
}
