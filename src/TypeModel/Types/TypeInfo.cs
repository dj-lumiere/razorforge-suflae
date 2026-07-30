using System;
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
    /// in-flight (`T`) value rather than a bound value. Entities are always in-flight
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

    /// <summary>The bare name without any baked-in generic-arg suffix (e.g. "List" for "List[Core.S64]").</summary>
    public string BareName
    {
        get
        {
            int idx = Name.IndexOf(value: '[');
            return idx >= 0 ? Name[..idx] : Name;
        }
    }

    /// <summary>
    /// Unqualified name with generic args recursively rendered unqualified too
    /// (e.g. <c>List[S64]</c>, <c>Dict[Text, S64]</c>). Built from the structural
    /// <see cref="TypeArguments"/> rather than the baked <see cref="Name"/>, which may embed
    /// fully-qualified inner args.
    /// </summary>
    public string ShortTypeName =>
        TypeArguments is { Count: > 0 } args
            ? $"{BareName}[{string.Join(separator: ", ",
                values: args.Select(selector: t => t.ShortTypeName))}]"
            : BareName;

    /// <summary>
    /// Module-qualified name with generic args recursively qualified too
    /// (e.g. <c>Core.List[Core.S64]</c>, <c>Core.Dict[Core.Text, Core.S64]</c>).
    /// </summary>
    public string QualifiedTypeName
    {
        get
        {
            string qualified = string.IsNullOrEmpty(value: Module) ? BareName : $"{Module}.{BareName}";
            return TypeArguments is { Count: > 0 } args
                ? $"{qualified}[{string.Join(separator: ", ",
                    values: args.Select(selector: t => t.QualifiedTypeName))}]"
                : qualified;
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

    /// <summary>
    /// Size in bytes of a value of this type at runtime (for allocation, GEP element strides,
    /// etc.). Default is pointer-sized — appropriate for heap-allocated kinds (entities,
    /// crashables, wrappers, protocols, routines). Records/variants/tuples override to sum
    /// their members.
    /// </summary>
    public virtual int SizeBytes(int pointerSize) => pointerSize;

    /// <summary>
    /// Aligns <paramref name="size"/> up to the next multiple of <paramref name="alignment"/>.
    /// </summary>
    protected static int AlignTo(int size, int alignment) =>
        (size + alignment - 1) / alignment * alignment;

    /// <summary>
    /// Computes the size of an LLVM type expressed as a string (e.g. an @llvm("…") backend
    /// annotation after template substitution). Handles primitives, fixed-size arrays
    /// (<c>[N x T]</c>), and inline struct literals (<c>{ T1, T2, … }</c>); the latter arise
    /// when a record without a direct backend type is substituted into another record's
    /// backend template (e.g. <c>Array[63, Text]</c> → <c>[63 x { ptr, i64, ptr }]</c>).
    /// Struct literals apply the same per-field alignment + final natural-alignment rule
    /// that <see cref="RecordTypeInfo.SizeBytes"/> uses.
    /// </summary>
    public static int SizeOfLlvmType(string llvmType, int pointerSize)
    {
        llvmType = llvmType.Trim();

        if (llvmType.StartsWith('[') && llvmType.EndsWith(']') && llvmType.Contains(" x "))
        {
            string inner = llvmType[1..^1];
            int sep = inner.IndexOf(value: " x ", comparisonType: StringComparison.Ordinal);
            int count = int.Parse(s: inner[..sep].Trim());
            int elemSize = SizeOfLlvmType(llvmType: inner[(sep + 3)..], pointerSize: pointerSize);
            return count * elemSize;
        }

        if (llvmType.StartsWith('{') && llvmType.EndsWith('}'))
        {
            int size = 0;
            int maxAlignment = 1;
            foreach (string field in SplitTopLevelCommas(input: llvmType[1..^1]))
            {
                int fieldSize = SizeOfLlvmType(llvmType: field, pointerSize: pointerSize);
                int alignment = Math.Max(val1: Math.Min(val1: fieldSize, val2: 16), val2: 1);
                maxAlignment = Math.Max(val1: maxAlignment, val2: alignment);
                size = AlignTo(size: size, alignment: alignment);
                size += fieldSize;
            }
            return AlignTo(size: size, alignment: maxAlignment);
        }

        return llvmType switch
        {
            "i1" => 1,
            "i8" => 1,
            "i16" => 2,
            "i32" => 4,
            "i64" => 8,
            "i128" => 16,
            "half" => 2,
            "float" => 4,
            "double" => 8,
            "fp128" => 16,
            "ptr" => pointerSize,
            "void" => 0,
            _ => SizeOfArbitraryInt(llvmType: llvmType)
        };
    }

    /// <summary>
    /// Size in bytes of an arbitrary-width integer type (<c>iN</c>, e.g. <c>i256</c>,
    /// <c>i512</c>) = ceil(N / 8). Throws for any non-<c>iN</c> type.
    /// </summary>
    private static int SizeOfArbitraryInt(string llvmType)
    {
        if (llvmType.Length > 1 && llvmType[index: 0] == 'i'
            && int.TryParse(s: llvmType[1..], result: out int bits) && bits > 0)
        {
            return (bits + 7) / 8;
        }

        throw new InvalidOperationException(
            message: $"Unknown LLVM type for size calculation: {llvmType}");
    }

    /// <summary>
    /// Splits <paramref name="input"/> on commas at brace/bracket depth 0. Used to parse
    /// the fields of an inline LLVM struct literal.
    /// </summary>
    private static IEnumerable<string> SplitTopLevelCommas(string input)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[index: i];
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return input[start..i];
                start = i + 1;
            }
        }
        if (start < input.Length) yield return input[start..];
    }
}
