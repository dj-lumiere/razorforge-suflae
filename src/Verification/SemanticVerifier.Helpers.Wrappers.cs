using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private bool IsNestedGrasping(Expression source)
    {
        while (true)
        {
            // Check if source is a member access expression (e.g., p.child)
            if (source is not MemberExpression member)
            {
                return false;
            }

            // Check if the object being accessed is an identifier
            if (member.Object is not IdentifierExpression id)
            {
                // Could be a chained member access, check recursively
                source = member.Object;
                continue;
            }

            // Look up the variable and check if its type is Grasped<T>
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            return varInfo != null &&
                   // Check if the variable's type is Grasped<T>
                   IsGraspedType(type: varInfo.Type);
        }
    }

    /// <summary>
    /// Checks if a type is a Grasped&lt;T&gt; token type.
    /// </summary>
    private static bool IsGraspedType(TypeSymbol type)
    {
        return type.Name == "Grasped" || type.Name.StartsWith(value: "Grasped[");
    }

    /// <summary>
    /// Checks if a type is a Claimed&lt;T&gt; token type.
    /// </summary>
    private static bool IsClaimedType(TypeSymbol type)
    {
        return type.Name == "Claimed" || type.Name.StartsWith(value: "Claimed[");
    }

    /// <summary>
    /// Checks if a type is a Shared&lt;T&gt; handle type.
    /// </summary>
    private static bool IsSharedType(TypeSymbol type)
    {
        return type.Name == "Shared";
    }

    /// <summary>
    /// Checks if a type is a Marked&lt;T&gt; handle type.
    /// </summary>
    private static bool IsMarkedType(TypeSymbol type)
    {
        return type.Name == "Marked";
    }

    /// <summary>
    /// All wrapper types that transparently forward to their inner type.
    /// </summary>
    private static readonly HashSet<string> WrapperTypes =
    [
        "Viewed",    // Read-only single-threaded token
        "Grasped",  // Exclusive write single-threaded token
        "Inspected", // Read-only multi-threaded token
        "Claimed",    // Exclusive write multi-threaded token
        "Shared",    // Reference-counted multi-threaded handle
        "Marked",    // Weak reference multi-threaded handle
        "Retained",  // Reference-counted handle
        "Tracked",   // Weak reference handle
        "Hijacked",  // Unmanaged raw pointer handle
        "Owned"      // Exclusive ownership wrapper (unique_ptr equivalent)
    ];

    /// <summary>
    /// Read-only wrapper types that can only access @readonly methods.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyWrapperTypes =
    [
        "Viewed", // Read-only single-threaded token
        "Inspected" // Read-only multi-threaded token
    ];

    /// <summary>
    /// Checks if a type is a wrapper type (Viewed, Grasped, Shared, etc.).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a wrapper type.</returns>
    private static bool IsWrapperType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return WrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewed, Inspected).
    /// </summary>
    /// <param name="type">The wrapper type to check.</param>
    /// <returns>True if the wrapper is read-only.</returns>
    private static bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewed&lt;T&gt;).
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <returns>The inner type, or null if not a wrapper or no type arguments.</returns>
    private static TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // Wrapper types have their inner type as the first type argument
        if (wrapperType.TypeArguments is { Count: > 0 })
        {
            return wrapperType.TypeArguments[index: 0];
        }

        return null;
    }

    /// <summary>
    /// Tries to look up a member variable on the inner type of a wrapper type.
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    private MemberVariableInfo? LookupMemberVariableOnWrapperInnerType(TypeSymbol wrapperType,
        string memberVariableName)
    {
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        return innerType switch
        {
            RecordTypeInfo record => record.LookupMemberVariable(
                memberVariableName: memberVariableName),
            EntityTypeInfo entity => entity.LookupMemberVariable(
                memberVariableName: memberVariableName),
            _ => null
        };
    }

    /// <summary>
    /// Validates that a method can be called through a read-only wrapper.
    /// Read-only wrappers (Viewed, Inspected) can only call @readonly methods.
    /// </summary>
    /// <param name="wrapperType">The wrapper type being used.</param>
    /// <param name="method">The method being called.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateReadOnlyWrapperMethodAccess(TypeSymbol wrapperType, RoutineInfo method,
        SourceLocation location)
    {
        if (!IsReadOnlyWrapper(type: wrapperType))
        {
            return; // Modifiable wrappers can access all methods
        }

        // Read-only wrappers can only access @readonly methods
        if (!method.IsReadOnly)
        {
            string wrapperName = GetBaseTypeName(typeName: wrapperType.Name);
            ReportError(code: SemanticDiagnosticCode.WritableMethodThroughReadOnlyWrapper,
                message:
                $"Cannot call writable method '{method.Name}' through read-only wrapper '{wrapperName}[T]'. " +
                $"Only @readonly methods are accessible.",
                location: location);
        }
    }

    /// <summary>
    /// Token types that cannot be returned from routines or stored in member variables.
    /// These are inline-only access tokens that must stay within their scope.
    /// </summary>
    private static readonly HashSet<string> InlineOnlyTokenTypes =
    [
        "Viewed", // Read-only single-threaded token
        "Grasped", // Exclusive write single-threaded token
        "Inspected", // Read-only multi-threaded token
        "Claimed" // Exclusive write multi-threaded token
    ];

    /// <summary>
    /// Token types that require uniqueness validation (cannot be passed twice in same call).
    /// </summary>
    private static readonly HashSet<string> ExclusiveTokenTypes =
    [
        "Grasped", // Cannot pass same Grasped token twice
        "Claimed" // Cannot pass same Claimed token twice
    ];

    /// <summary>
    /// Wrapper types that require an explicit verb at every copy site instead of being
    /// implicitly assignable. The dictionary value is the verb the user must write.
    /// `Hijacked[T]` is excluded because it is a raw pointer and copies bitwise.
    /// See <c>RazorForge-Wiki/docs/Records.md#copy-semantics</c>.
    /// </summary>
    private static readonly Dictionary<string, string> NonTriviallyCopyableWrappers =
        new(StringComparer.Ordinal)
        {
            ["Owned"] = "steal a",
            ["Retained"] = "a.retain()",
            ["Tracked"] = "a.track()",
            ["Shared"] = "a.share()",
            ["Marked"] = "a.mark()",
            ["Viewed"] = "(none — scoped, can't escape)",
            ["Grasped"] = "(none — scoped, can't escape)",
            ["Inspected"] = "(none — scoped, can't escape)",
            ["Claimed"] = "(none — scoped, can't escape)",
        };

    /// <summary>
    /// Returns true when the type can be copied with a bitwise <c>store</c> with no
    /// observable side-effects (no reference-count bump, no ownership transfer, no
    /// scoped-token escape). Used by the implicit-copy diagnostic in
    /// <see cref="SemanticVerifier.AnalyzeVariableDeclaration"/> and assignment analysis.
    /// </summary>
    /// <param name="type">Type to classify.</param>
    /// <param name="visited">Cycle guard for self-referential record definitions.</param>
    private bool IsTriviallyCopyable(TypeSymbol type, HashSet<string>? visited = null)
    {
        if (type is ErrorTypeInfo or GenericParameterTypeInfo || type.IsBlank)
        {
            // Unknown / void types — be permissive so we do not double-report.
            return true;
        }

        string baseName = GetBaseTypeName(typeName: type.Name);
        if (NonTriviallyCopyableWrappers.ContainsKey(key: baseName))
        {
            return false;
        }

        // Hijacked[T], primitives, @llvm("...") records — trivially copyable bitwise.
        if (type is RecordTypeInfo record)
        {
            // Generic-definition records (no concrete type args) have no member type info worth
            // walking — treat as trivially copyable. Concrete instances re-enter via the member
            // walk below.
            if (record.IsGenericDefinition && record.TypeArguments is not { Count: > 0 })
            {
                return true;
            }

            // Cycle guard: a record that recursively contains itself (only possible through a
            // wrapper / entity field) would loop here without it.
            visited ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visited.Add(item: record.FullName))
            {
                return true;
            }

            foreach (MemberVariableInfo member in record.MemberVariables)
            {
                if (!IsTriviallyCopyable(type: member.Type, visited: visited))
                {
                    return false;
                }
            }
            return true;
        }

        // Tuples — anonymous records; mirror the per-field walk.
        if (type is TupleTypeInfo tuple)
        {
            visited ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visited.Add(item: tuple.FullName))
            {
                return true;
            }
            foreach (TypeInfo element in tuple.ElementTypes)
            {
                if (!IsTriviallyCopyable(type: element, visited: visited))
                {
                    return false;
                }
            }
            return true;
        }

        // Entities (raw `T`) cannot appear in copy positions — separate diagnostics catch them.
        // Anything we did not recognise falls back to trivially copyable so this pass does not
        // become noisy during the Phase 1 rollout.
        return true;
    }

    /// <summary>
    /// Locates the first non-trivially-copyable wrapper inside a type's field tree.
    /// Used to format the hint message for the implicit-copy diagnostic.
    /// Returns null when the type itself is trivially copyable.
    /// </summary>
    /// <param name="type">Type to classify.</param>
    /// <returns>The offending wrapper's base name (e.g. <c>"Retained"</c>) and the path
    /// of field names leading to it, or null when no offender exists.</returns>
    private (string Wrapper, string Path)? FindNonTriviallyCopyableWrapper(TypeSymbol type)
    {
        return FindCore(type: type, prefix: "", visited: new HashSet<string>(StringComparer.Ordinal));

        (string, string)? FindCore(TypeSymbol type, string prefix, HashSet<string> visited)
        {
            string baseName = GetBaseTypeName(typeName: type.Name);
            if (NonTriviallyCopyableWrappers.ContainsKey(key: baseName))
            {
                return (baseName, prefix.Length == 0 ? "<value>" : prefix);
            }
            if (type is RecordTypeInfo record &&
                !(record.IsGenericDefinition && record.TypeArguments is not { Count: > 0 }))
            {
                if (!visited.Add(item: record.FullName))
                    return null;
                foreach (MemberVariableInfo member in record.MemberVariables)
                {
                    string childPath = prefix.Length == 0 ? member.Name : $"{prefix}.{member.Name}";
                    var found = FindCore(type: member.Type, prefix: childPath, visited: visited);
                    if (found != null) return found;
                }
            }
            else if (type is TupleTypeInfo tuple)
            {
                if (!visited.Add(item: tuple.FullName))
                    return null;
                for (int i = 0; i < tuple.ElementTypes.Count; i++)
                {
                    string childPath = prefix.Length == 0 ? $".{i}" : $"{prefix}.{i}";
                    var found = FindCore(type: tuple.ElementTypes[index: i], prefix: childPath, visited: visited);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
