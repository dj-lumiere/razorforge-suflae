using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Enums;

namespace TypeModel.Types;

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
    public List<string>? GenericParameters { get; init; }

    /// <summary>Generic constraints on type parameters.</summary>
    public List<GenericConstraintDeclaration>? GenericConstraints { get; init; }

    /// <summary>Whether this is a generic type definition (has unsubstituted type parameters).</summary>
    public bool IsGenericDefinition => GenericParameters is { Count: > 0 };

    /// <summary>For resolved generics, the type arguments used.</summary>
    public List<TypeInfo>? TypeArguments { get; init; }

    /// <summary>Whether this is a resolved generic type.</summary>
    public bool IsGenericResolution => TypeArguments is { Count: > 0 };

    /// <summary>Whether this is the Blank (unit/void) type.</summary>
    public bool IsBlank => Name == "Blank";

    /// <summary>
    /// True for types whose implicit/synthesized default constructor produces an
    /// in-flight (`?T`) value rather than a bound value. Entities are always in-flight
    /// at construction (the freshly-created handle is unbound until a `var`/`let` /
    /// field/param consumes it). Records and other value types default to false.
    /// Used by creator analysis to propagate <see cref="SyntaxTree.Expression.IsInFlight"/>
    /// when no user-declared <c>$create</c> routine resolves at the call site.
    /// </summary>
    public virtual bool ImplicitConstructorReturnsInFlight => false;

    /// <summary>
    /// Set to true when this concrete generic instance was first created during stdlib body
    /// analysis. Such types are excluded from <c>AllConcreteGenericInstances</c> and GMP
    /// until user code references them, at which point the registry clears this flag
    /// and enqueues the type for monomorphization.
    /// </summary>
    public bool IsStdlibLazy { get; internal set; }

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

            // Generic resolutions already have type args in Name (e.g., "Hijacked[U8]"),
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
    public abstract TypeInfo CreateInstance(List<TypeInfo> typeArguments);
}
