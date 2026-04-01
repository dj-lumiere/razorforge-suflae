namespace SemanticAnalysis;

using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private bool IsNestedHijacking(Expression source)
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
            return IsNestedHijacking(source: member.Object);
        }

        // Look up the variable and check if its type is Hijacked<T>
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo == null)
        {
            return false;
        }

        // Check if the variable's type is Hijacked<T>
        return IsHijackedType(type: varInfo.Type);
    }

    /// <summary>
    /// Checks if a type is a Hijacked&lt;T&gt; token type.
    /// </summary>
    private static bool IsHijackedType(TypeSymbol type)
    {
        return type.Name == "Hijacked" || type.Name.StartsWith(value: "Hijacked[");
    }

    /// <summary>
    /// Checks if a type is a Seized&lt;T&gt; token type.
    /// </summary>
    private static bool IsSeizedType(TypeSymbol type)
    {
        return type.Name == "Seized" || type.Name.StartsWith(value: "Seized[");
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
        "Hijacked",  // Exclusive write single-threaded token
        "Inspected", // Read-only multi-threaded token
        "Seized",    // Exclusive write multi-threaded token
        "Shared",    // Reference-counted multi-threaded handle
        "Marked",    // Weak reference multi-threaded handle
        "Retained",  // Reference-counted handle
        "Tracked",   // Weak reference handle
        "Snatched"   // Unmanaged raw pointer handle
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
    /// Checks if a type is a wrapper type (Viewed, Hijacked, Shared, etc.).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a wrapper type.</returns>
    private bool IsWrapperType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return WrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewed, Inspected).
    /// </summary>
    /// <param name="type">The wrapper type to check.</param>
    /// <returns>True if the wrapper is read-only.</returns>
    private bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewed&lt;T&gt;).
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <returns>The inner type, or null if not a wrapper or no type arguments.</returns>
    private TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
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
    /// Tries to look up a method on the inner type of a wrapper type.
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <param name="methodName">The name of the method to look up.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    private RoutineInfo? LookupMethodOnWrapperInnerType(TypeSymbol wrapperType, string methodName)
    {
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        // Look up the method on the inner type
        return _registry.LookupRoutine(fullName: $"{innerType.Name}.{methodName}");
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
        "Hijacked", // Exclusive write single-threaded token
        "Inspected", // Read-only multi-threaded token
        "Seized" // Exclusive write multi-threaded token
    ];

    /// <summary>
    /// RC handles + Snatched: allowed as record member variable types.
    /// Scoped tokens (Viewed, Hijacked, Inspected, Seized) remain banned.
    /// </summary>
    private static readonly HashSet<string> StorableWrapperTypes =
    [
        "Snatched", // Unmanaged raw pointer handle
        "Retained", // Reference-counted handle
        "Shared", // Reference-counted multi-threaded handle
        "Tracked", // Weak reference handle
        "Marked" // Weak reference multi-threaded handle
    ];

    /// <summary>
    /// Token types that require uniqueness validation (cannot be passed twice in same call).
    /// </summary>
    private static readonly HashSet<string> ExclusiveTokenTypes =
    [
        "Hijacked", // Cannot pass same Hijacked token twice
        "Seized" // Cannot pass same Seized token twice
    ];
}
