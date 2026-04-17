namespace SemanticVerification.Types;

using Enums;
using Symbols;

/// <summary>
/// Type information for crashable types — throwable error entities.
/// Always heap-allocated (entity semantics). Automatically conforms to the Crashable protocol.
/// Must provide crash_message() -> Text; crash_title() is synthesized from the type name.
/// </summary>
public sealed class CrashableTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Crashable;

    /// <summary>Member variables declared in this crashable type.</summary>
    public IReadOnlyList<MemberVariableInfo> MemberVariables { get; set; } = [];

    /// <summary>Protocols this crashable type implements (always includes Crashable).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; set; } = [];

    /// <summary>
    /// The synthesized crash title (sentence-cased type name, e.g. "Network error" for NetworkError).
    /// Computed once and stored here for codegen use.
    /// </summary>
    public string CrashTitle { get; init; }


    /// <summary>
    /// Looks up a member variable by name.
    /// </summary>
    public MemberVariableInfo? LookupMemberVariable(string memberVariableName) =>
        MemberVariables.FirstOrDefault(predicate: f => f.Name == memberVariableName);

    /// <summary>
    /// Initializes a new instance of <see cref="CrashableTypeInfo"/>.
    /// </summary>
    /// <param name="name">The type name.</param>
    public CrashableTypeInfo(string name) : base(name: name)
    {
        CrashTitle = SynthesizeCrashTitle(typeName: name);
    }

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Crashable type '{Name}' cannot be resolved with type arguments.");
    }

    /// <summary>
    /// Converts a CamelCase type name to a sentence-cased crash title.
    /// Examples: NetworkError → "Network error", VerificationFailedError → "Verification failed error"
    /// </summary>
    public static string SynthesizeCrashTitle(string typeName)
    {
        if (string.IsNullOrEmpty(value: typeName))
            return typeName;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < typeName.Length; i++)
        {
            if (i > 0 && char.IsUpper(c: typeName[index: i]))
                sb.Append(value: ' ');
            sb.Append(value: typeName[index: i]);
        }

        string spaced = sb.ToString().ToLowerInvariant();
        return char.ToUpperInvariant(c: spaced[0]) + spaced[1..];
    }
}
