namespace Compilers.Analysis.Types;

using Enums;
using Shared.AST;

/// <summary>
/// Base class for all type information in the TypeRegistry.
/// </summary>
public abstract class TypeInfo
{
    /// <summary>The name of the type (e.g., "s32", "List", "Point").</summary>
    public string Name { get; }

    /// <summary>The category of this type.</summary>
    public abstract TypeCategory Category { get; }

    /// <summary>Generic type parameters, if any (e.g., ["T"] for List&lt;T&gt;).</summary>
    public IReadOnlyList<string>? GenericParameters { get; init; }

    /// <summary>Generic constraints on type parameters.</summary>
    public IReadOnlyList<GenericConstraintDeclaration>? GenericConstraints { get; init; }

    /// <summary>Whether this is a generic type definition (has unsubstituted type parameters).</summary>
    public bool IsGenericDefinition => GenericParameters is { Count: > 0 };

    /// <summary>For instantiated generics, the type arguments used.</summary>
    public IReadOnlyList<TypeInfo>? TypeArguments { get; init; }

    /// <summary>Whether this is an instantiated generic type.</summary>
    public bool IsGenericInstantiation => TypeArguments is { Count: > 0 };

    /// <summary>Whether this is the Blank (unit/void) type.</summary>
    public bool IsBlank => Name == "Blank";

    /// <summary>The visibility of this type.</summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Public;

    /// <summary>Source location where this type is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The namespace/module this type belongs to.</summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// The fully qualified name of this type (namespace + name + generic args).
    /// </summary>
    public string FullName
    {
        get
        {
            string baseName = string.IsNullOrEmpty(value: Namespace)
                ? Name
                : $"{Namespace}.{Name}";

            if (TypeArguments is not { Count: > 0 })
            {
                return baseName;
            }

            string args = string.Join(separator: ", ",
                values: TypeArguments.Select(selector: t => t.FullName));
            return $"{baseName}<{args}>";
        }
    }

    protected TypeInfo(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Creates an instantiated version of this generic type with the given type arguments.
    /// </summary>
    public abstract TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments);
}
