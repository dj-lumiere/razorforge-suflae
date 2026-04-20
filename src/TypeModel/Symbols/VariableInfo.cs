using TypeModel.Types;
using SyntaxTree;

namespace TypeModel.Symbols;

/// <summary>
/// Information about a variable in a scope.
/// </summary>
public sealed class VariableInfo
{
    /// <summary>The name of the variable.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the variable.</summary>
    public TypeInfo Type { get; }

    /// <summary>Whether this variable is modifiable.
    /// Presets are not modifiable (IsModifiable=false).
    /// All other variables are modifiable (IsModifiable=true).</summary>
    public bool IsModifiable { get; init; }

    /// <summary>Whether this is a preset (build-time constant).
    /// Presets are always frozen (IsModifiable=false) and must be initialized with constant expressions.</summary>
    public bool IsPreset { get; init; }

    /// <summary>The module this variable belongs to.</summary>
    public string? Module { get; init; }

    /// <summary>The module-qualified name (e.g., "Core.S8_MIN").</summary>
    public string QualifiedName => string.IsNullOrEmpty(value: Module)
        ? Name
        : $"{Module}.{Name}";

    /// <summary>Source location where this variable is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// For preset declarations: the constant value expression to inline at use sites.
    /// Null for ordinary variables.
    /// </summary>
    public Expression? PresetValue { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VariableInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="type">The resolved type of the variable.</param>
    public VariableInfo(string name, TypeInfo type)
    {
        Name = name;
        Type = type;
    }
}
