using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private const string ModifyingWrapperName = "Modifying";
    private const string ClaimingWrapperName = "Claiming";
    private const string ViewingWrapperName = "Viewing";
    private const string InspectingWrapperName = "Inspecting";
    private const string ScopedNoEscapeHint = "(none — scoped, can't escape)";

    private bool IsNestedModifying(Expression source)
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

            // Look up the variable and check if its type is Modifying<T>
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            return varInfo != null &&
                   // Check if the variable's type is Modifying<T>
                   IsModifyingType(type: varInfo.Type);
        }
    }

    /// <summary>
    /// Checks if a type is a Modifying&lt;T&gt; token type.
    /// </summary>
    private static bool IsModifyingType(TypeSymbol type)
    {
        return type.Name == ModifyingWrapperName || type.Name.StartsWith(value: ModifyingWrapperName + "[");
    }

    /// <summary>
    /// Checks if a type is a Claiming&lt;T&gt; token type.
    /// </summary>
    private static bool IsClaimingType(TypeSymbol type)
    {
        return type.Name == ClaimingWrapperName || type.Name.StartsWith(value: ClaimingWrapperName + "[");
    }

    /// <summary>
    /// Checks if a type is a Shared&lt;T&gt; handle type.
    /// </summary>
    private static bool IsSharedType(TypeSymbol type)
    {
        return type.Name == "Shared";
    }

    /// <summary>
    /// Checks if a type is a Watched&lt;T&gt; handle type.
    /// </summary>
    private static bool IsWatchedType(TypeSymbol type)
    {
        return type.Name == "Watched";
    }

    /// <summary>
    /// All wrapper types that transparently forward to their inner type.
    /// </summary>
    private static readonly HashSet<string> WrapperTypes =
    [
        ViewingWrapperName,    // Read-only single-threaded token
        ModifyingWrapperName,  // Exclusive write single-threaded token
        InspectingWrapperName, // Read-only multi-threaded token
        ClaimingWrapperName,   // Exclusive write multi-threaded token
        "Shared",    // Reference-counted multi-threaded handle
        "Watched",   // Weak reference multi-threaded handle
        "Retained",  // Reference-counted handle
        "Tracked",   // Weak reference handle
        "Hijacked",  // Unmanaged raw pointer handle
    ];

    /// <summary>
    /// Read-only wrapper types that can only access @readonly methods.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyWrapperTypes =
    [
        ViewingWrapperName, // Read-only single-threaded token
        InspectingWrapperName // Read-only multi-threaded token
    ];

    /// <summary>
    /// Checks if a type is a wrapper type (Viewing, Modifying, Shared, etc.).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a wrapper type.</returns>
    private static bool IsWrapperType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return WrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewing, Inspecting).
    /// </summary>
    /// <param name="type">The wrapper type to check.</param>
    /// <returns>True if the wrapper is read-only.</returns>
    private static bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewing&lt;T&gt;).
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
    private static MemberVariableInfo? LookupMemberVariableOnWrapperInnerType(TypeSymbol wrapperType,
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
    /// Read-only wrappers (Viewing, Inspecting) can only call @readonly methods.
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
        ViewingWrapperName, // Read-only single-threaded token
        ModifyingWrapperName, // Mutable (non-exclusive) single-threaded token
        InspectingWrapperName, // Read-only multi-threaded token
        ClaimingWrapperName // Exclusive write multi-threaded token
    ];

    /// <summary>
    /// Token types that require uniqueness validation (cannot be passed twice in same call).
    /// Only the multi-threaded exclusive write token qualifies: single-threaded Modifying is
    /// non-exclusive (shared-mutable aliasing is harmless without a second thread).
    /// </summary>
    private static readonly HashSet<string> ExclusiveTokenTypes =
    [
        ClaimingWrapperName // Cannot pass same Claiming token twice
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
            ["Retained"] = "a.retain()",
            ["Tracked"] = "a.track()",
            ["Shared"] = "a.share()",
            ["Watched"] = "a.watch()",
            [ViewingWrapperName] = ScopedNoEscapeHint,
            [ModifyingWrapperName] = ScopedNoEscapeHint,
            [InspectingWrapperName] = ScopedNoEscapeHint,
            [ClaimingWrapperName] = ScopedNoEscapeHint,
        };

    /// <summary>
    /// Returns true when the type can appear in an implicit-copy position (var binding,
    /// non-<c>steal</c> argument pass, non-<c>?T</c> return, <c>with</c> base). The check
    /// is "obeys <c>Assignable</c>" — auto-derived for records whose @llvm layout has no
    /// <c>ptr</c>, explicitly opt-in for raw-pointer wrappers (<c>Hijacked</c>, <c>CPtr</c>),
    /// never auto-derived for ownership-bearing wrappers (<c>Retained</c>,
    /// <c>Tracked</c>, scoped tokens). The recursive structural walk that this used to do
    /// is now subsumed by the protocol's auto-derivation rule.
    /// </summary>
    private static bool IsTriviallyCopyable(TypeSymbol type)
    {
        if (type is ErrorTypeInfo or GenericParameterTypeInfo || type.IsBlank)
        {
            // Unknown / void types — be permissive so we do not double-report.
            return true;
        }

        // Generic-definition wrappers / records (no concrete type args) appear when SA walks
        // generic-def bodies. The concrete instantiations are re-analysed via monomorphisation,
        // so suppress here to avoid duplicate / placeholder-shaped diagnostics on stdlib.
        if (type is RecordTypeInfo { IsGenericDefinition: true, TypeArguments: null or { Count: 0 } })
        {
            return true;
        }

        // Tuples — anonymous; auto-derive cascades only when every element does.
        if (type is TupleTypeInfo tuple)
        {
            return tuple.ElementTypes.All(predicate: IsTriviallyCopyable);
        }

        // Records / choices / flags / entities carry ImplementedProtocols populated by
        // ProtocolConformanceAnalyzer (explicit + auto-derived Assignable).
        List<TypeSymbol>? implemented = type switch
        {
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };

        if (implemented != null)
        {
            return implemented.Any(predicate: p => p.Name == "Assignable");
        }

        // Anything we did not recognise falls back to trivially copyable so this pass does
        // not become noisy on unexpected shapes.
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
    private static (string Wrapper, string Path)? FindNonTriviallyCopyableWrapper(TypeSymbol type)
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
                !(record is { IsGenericDefinition: true, TypeArguments: not { Count: > 0 } }))
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
