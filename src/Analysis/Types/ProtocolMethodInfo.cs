namespace SemanticAnalysis.Types;

using Enums;
using SyntaxTree;

/// <summary>
/// Information about a method signature in a protocol.
/// </summary>
public sealed class ProtocolMethodInfo
{
    /// <summary>The name of the method.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether this is an instance method (has me parameter) or type-level method.
    /// </summary>
    public bool IsInstanceMethod { get; init; } = true;

    /// <summary>The modification category for this method.</summary>
    public ModificationCategory Modification { get; init; } = ModificationCategory.Migratable;

    /// <summary>Parameter types (excluding me for instance methods).</summary>
    public IReadOnlyList<TypeInfo> ParameterTypes { get; init; } = [];

    /// <summary>Parameter names.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>Return type, or null for void.</summary>
    public TypeInfo? ReturnType { get; init; }

    /// <summary>Whether this method can fail (has ! suffix).</summary>
    public bool IsFailable { get; init; }

    /// <summary>The generation kind for this method (None, Generated, or Innate).</summary>
    public ProtocolRoutineKind GenerationKind { get; init; } = ProtocolRoutineKind.None;

    /// <summary>Whether this method has a default implementation.</summary>
    public bool HasDefaultImplementation { get; init; }

    /// <summary>Source location where this method is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolMethodInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the protocol method.</param>
    public ProtocolMethodInfo(string name)
    {
        Name = name;
    }
}
