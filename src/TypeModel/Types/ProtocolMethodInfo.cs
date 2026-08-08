using System.Collections.Generic;
using Verification.Enums;
using SyntaxTree;
using TypeModel.Enums;

namespace TypeModel.Types;

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

    /// <summary>The mutation category for this method.</summary>
    public MutationCategory Mutation { get; init; } = MutationCategory.Migratable;

    /// <summary>Parameter types (excluding me for instance methods).</summary>
    public List<TypeInfo> ParameterTypes { get; init; } = [];

    /// <summary>Parameter names.</summary>
    public List<string> ParameterNames { get; init; } = [];

    /// <summary>Return type, or null for void.</summary>
    public TypeInfo? ReturnType { get; init; }

    /// <summary>Whether this method can fail (has ! suffix).</summary>
    public bool IsFailable { get; init; }

    /// <summary>The generation kind for this method (None, Generated, or Innate).</summary>
    public ProtocolRoutineKind GenerationKind { get; init; } = ProtocolRoutineKind.None;

    /// <summary>Whether this method has a default implementation.</summary>
    public bool HasDefaultImplementation { get; init; }

    /// <summary>
    /// True when this entry is an auto-derived failable variant (`try_X`, `check_X`,
    /// `lookup_X`) synthesized by <c>FillProtocolMethods</c> from a failable original
    /// (`X!`). Such entries exist for call-site resolution but are NOT conformance
    /// obligations — the implementer only needs to provide the failable original.
    /// </summary>
    public bool IsAutoDerivedVariant { get; init; }

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
