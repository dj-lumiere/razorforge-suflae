using Compilers.Shared.AST;
using TypeSymbol = Compilers.Analysis.Types.TypeInfo;

namespace Compilers.Analysis.Symbols;

/// <summary>
/// Information about a variable in a scope.
/// </summary>
public sealed class VariableInfo
{
    /// <summary>The name of the variable.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the variable.</summary>
    public TypeSymbol Type { get; }

    /// <summary>Whether this variable is mutable (var) or immutable (let).</summary>
    public bool IsMutable { get; init; }

    /// <summary>Whether this is a preset (compile-time constant).</summary>
    public bool IsPreset { get; init; }

    /// <summary>Source location where this variable is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VariableInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="type">The resolved type of the variable.</param>
    public VariableInfo(string name, TypeSymbol type)
    {
        Name = name;
        Type = type;
    }
}
