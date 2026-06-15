using SyntaxTree;
using TypeModel.Types;

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
    /// True when this preset is a constant aggregate (<c>Array[T, N]</c> or <c>BitArray[N]</c>). Such
    /// presets are NOT inlined at use sites (which would rebuild the whole array per reference — the
    /// cause of the fun_bench OOM where an <c>Array[U16, 1000]</c> DPD table was reconstructed as a
    /// heap List on every call). Instead the identifier is kept so codegen emits ONE module-level
    /// <c>@preset.*</c> constant and indexes into it. Scalar <c>@llvm</c> presets stay inlined.
    /// </summary>
    public bool IsPresettableAggregate =>
        IsPreset
        && PresetValue is ListLiteralExpression
        && Type is RecordTypeInfo record
        && BaseTypeName(name: record.GenericDefinition?.Name ?? record.Name) is "Array" or "BitArray";

    /// <summary>
    /// Strips generic arguments and module qualifiers from a type name
    /// (e.g. <c>Core.Array[U16, 1000]</c> -&gt; <c>Array</c>).
    /// </summary>
    private static string BaseTypeName(string name)
    {
        int bracket = name.IndexOf(value: '[');
        string bare = bracket > 0 ? name[..bracket] : name;
        int dot = bare.LastIndexOf(value: '.');
        return dot >= 0 ? bare[(dot + 1)..] : bare;
    }

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
