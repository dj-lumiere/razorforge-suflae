namespace SemanticVerification.Types;

using Enums;
using SyntaxTree;

/// <summary>
/// Base class for all type information in the TypeRegistry.
/// </summary>
public abstract class TypeInfo
{
    /// <summary>The name of the type (e.g., "S32", "List", "Point").</summary>
    public string Name { get; }

    /// <summary>The category of this type.</summary>
    public abstract TypeCategory Category { get; }

    /// <summary>Generic type parameters, if any (e.g., ["T"] for List&lt;T&gt;).</summary>
    public IReadOnlyList<string>? GenericParameters { get; init; }

    /// <summary>Generic constraints on type parameters.</summary>
    public IReadOnlyList<GenericConstraintDeclaration>? GenericConstraints { get; init; }

    /// <summary>Whether this is a generic type definition (has unsubstituted type parameters).</summary>
    public bool IsGenericDefinition => GenericParameters is { Count: > 0 };

    /// <summary>For resolved generics, the type arguments used.</summary>
    public IReadOnlyList<TypeInfo>? TypeArguments { get; init; }

    /// <summary>Whether this is a resolved generic type.</summary>
    public bool IsGenericResolution => TypeArguments is { Count: > 0 };

    /// <summary>Whether this is the Blank (unit/void) type.</summary>
    public bool IsBlank => Name == "Blank";

    /// <summary>The visibility of this type.</summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Open;

    /// <summary>Source location where this type is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The module this type belongs to.</summary>
    public string? Module { get; init; }

    /// <summary>
    /// The fully qualified name of this type (module + name + generic args).
    /// </summary>
    public string FullName
    {
        get
        {
            string baseName = string.IsNullOrEmpty(value: Module)
                ? Name
                : $"{Module}.{Name}";

            // Generic resolutions already have type args in Name (e.g., "Snatched[U8]"),
            // so only append TypeArguments for generic definitions where Name is bare
            if (TypeArguments is not { Count: > 0 } || Name.Contains(value: '['))
            {
                return baseName;
            }

            string args = string.Join(separator: ", ",
                values: TypeArguments.Select(selector: t => t.FullName));
            return $"{baseName}[{args}]";
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeInfo"/> class.
    /// </summary>
    /// <param name="name">The type name.</param>
    protected TypeInfo(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Creates a resolved version of this generic type with the given type arguments.
    /// </summary>
    public abstract TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments);
}
