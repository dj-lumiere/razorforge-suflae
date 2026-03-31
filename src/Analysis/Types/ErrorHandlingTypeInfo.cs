namespace SemanticAnalysis.Types;

using Enums;

/// <summary>
/// Type information for builder-generated error handling types (Maybe, Result, Lookup).
/// These work similar to variants but have special semantics:
/// - No looping over cases
/// - Cannot be stored/returned (must be immediately pattern matched)
/// - Generated automatically from `!` functions
/// </summary>
public sealed class ErrorHandlingTypeInfo : TypeInfo
{
    /// <summary>
    /// The category of this type.
    /// </summary>
    public override TypeCategory Category => TypeCategory.ErrorHandling;

    /// <summary>The kind of error handling type.</summary>
    public ErrorHandlingKind Kind { get; }

    /// <summary>The success value type (T in Maybe&lt;T&gt;, Result&lt;T&gt;, Lookup&lt;T&gt;).</summary>
    public TypeInfo ValueType { get; }

    /// <summary>
    /// For Result and Lookup, the error type.
    /// Always a type that obeys Crashable protocol.
    /// </summary>
    public TypeInfo? ErrorType { get; init; }

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public ErrorHandlingTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorHandlingTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the error handling type.</param>
    /// <param name="kind">The kind of error handling type.</param>
    /// <param name="valueType">The success value type.</param>
    public ErrorHandlingTypeInfo(string name, ErrorHandlingKind kind, TypeInfo valueType) :
        base(name: name)
    {
        Kind = kind;
        ValueType = valueType;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown if not exactly 1 type argument is provided.</exception>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        if (typeArguments.Count != 1)
        {
            throw new ArgumentException(
                message: $"Error handling type '{Name}' expects exactly 1 type argument.");
        }

        TypeInfo valueType = typeArguments[index: 0];
        string resolvedName = $"{Kind}[{valueType.Name}]";

        return new ErrorHandlingTypeInfo(name: resolvedName, kind: Kind, valueType: valueType)
        {
            ErrorType = ErrorType,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module
        };
    }

    /// <summary>
    /// Well-known error handling type definitions.
    /// </summary>
    public static class WellKnown
    {
        /// <summary>
        /// Generic Maybe&lt;T&gt; definition.
        /// Pattern matches with: is None, else value
        /// </summary>
        public static ErrorHandlingTypeInfo MaybeDefinition { get; } = new(name: "Maybe",
            kind: ErrorHandlingKind.Maybe,
            valueType: new TypeParameterPlaceholder(name: "T")) { GenericParameters = ["T"] };

        /// <summary>
        /// Generic Result&lt;T&gt; definition.
        /// Pattern matches with: is Crashable e, else value
        /// </summary>
        public static ErrorHandlingTypeInfo ResultDefinition { get; } = new(name: "Result",
            kind: ErrorHandlingKind.Result,
            valueType: new TypeParameterPlaceholder(name: "T")) { GenericParameters = ["T"] };

        /// <summary>
        /// Generic Lookup&lt;T&gt; definition.
        /// Pattern matches with: is Crashable e, is None, else value
        /// </summary>
        public static ErrorHandlingTypeInfo LookupDefinition { get; } = new(name: "Lookup",
            kind: ErrorHandlingKind.Lookup,
            valueType: new TypeParameterPlaceholder(name: "T")) { GenericParameters = ["T"] };
    }
}
