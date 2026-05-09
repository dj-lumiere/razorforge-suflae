using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private TypeSymbol AnalyzeInsertedTextExpression(InsertedTextExpression insertedText)
    {
        foreach (InsertedTextPart part in insertedText.Parts)
        {
            if (part is ExpressionPart exprPart)
            {
                AnalyzeExpression(expression: exprPart.Expression);
                ValidateFTextFormatSpec(formatSpec: exprPart.FormatSpec,
                    location: exprPart.Location);
            }
        }

        return _registry.LookupType(name: "Text") ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Validates that an f-text format specifier is one of the allowed values.
    /// Valid: null (none), "=", "?", "=?". Invalid: "?=" (wrong order), anything else.
    /// </summary>
    private void ValidateFTextFormatSpec(string? formatSpec, SourceLocation location)
    {
        if (formatSpec is null or "=" or "?" or "=?")
        {
            return;
        }

        if (formatSpec == "?=")
        {
            ReportError(code: SemanticDiagnosticCode.InvalidFTextFormatSpec,
                message:
                "Invalid f-text format specifier '?='. The correct order is '=?' (name display first, then diagnose).",
                location: location);
            return;
        }

        ReportError(code: SemanticDiagnosticCode.InvalidFTextFormatSpec,
            message:
            $"Invalid f-text format specifier '{formatSpec}'. F-text only supports '=' (name display), '?' (diagnose), and '=?' (combined).",
            location: location);
    }

/// <summary>
    /// Substitutes type parameters in a type based on a generic resolution.
    /// For example, if genericType is List&lt;S32&gt; and type is T, returns S32.
    /// </summary>
    /// <param name="type">The type that may contain type parameters.</param>
    /// <param name="genericType">The resolved generic type providing type argument bindings.</param>
    /// <returns>The substituted type.</returns>
    private TypeSymbol SubstituteTypeParameters(TypeSymbol type, TypeSymbol genericType)
    {
        if (genericType.TypeArguments == null || genericType.TypeArguments.Count == 0)
        {
            return type;
        }

        // Get the generic definition to find type parameter names
        TypeSymbol? genericDef = GetGenericDefinition(resolution: genericType);
        if (genericDef == null)
        {
            return type;
        }

        // Build a mapping from type parameter names to actual types
        IReadOnlyList<string>? typeParamNames = genericDef.GenericParameters;
        if (typeParamNames == null || typeParamNames.Count != genericType.TypeArguments.Count)
        {
            return type;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < typeParamNames.Count; i++)
        {
            substitutions[key: typeParamNames[index: i]] = genericType.TypeArguments[index: i];
        }

        return SubstituteWithMapping(type: type, substitutions: substitutions);
    }

    /// <summary>
    /// Gets the generic definition from a resolution.
    /// </summary>
    private TypeSymbol? GetGenericDefinition(TypeSymbol resolution)
    {
        if (!resolution.IsGenericResolution)
        {
            return null;
        }

        // Extract base name (e.g., "List" from "List[S32]")
        string baseName = GetBaseTypeName(typeName: resolution.Name);
        TypeSymbol? def = _registry.LookupType(name: baseName);
        // Try slash-qualified module path lookup for non-Core types (e.g., "Collections/Deque")
        if (def == null && !string.IsNullOrEmpty(value: resolution.Module))
        {
            def = _registry.LookupType(name: $"{resolution.Module}.{baseName}");
        }

        return def;
    }

    /// <summary>
    /// Substitutes type parameters using a mapping.
    /// </summary>
    private TypeSymbol SubstituteWithMapping(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        // Direct type parameter replacement
        if (substitutions.TryGetValue(key: type.Name, value: out TypeSymbol? replacement))
        {
            return replacement;
        }

        // For generic resolutions, recursively substitute in type arguments
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            var substitutedArgs = new List<TypeSymbol>();
            bool anyChanged = false;

            foreach (TypeSymbol arg in type.TypeArguments)
            {
                TypeSymbol substitutedArg =
                    SubstituteWithMapping(type: arg, substitutions: substitutions);
                substitutedArgs.Add(item: substitutedArg);
                if (!ReferenceEquals(objA: substitutedArg, objB: arg))
                {
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                // Create a new resolution with substituted arguments
                TypeSymbol? baseDef = GetGenericDefinition(resolution: type);
                if (baseDef != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: baseDef,
                        typeArguments: substitutedArgs);
                }
            }
        }

        return type;
    }

    /// <summary>
    /// Analyzes a steal expression (RazorForge only).
    /// Validates that the operand can be stolen and returns the stolen type.
    /// </summary>
    /// <param name="steal">The steal expression to analyze.</param>
    /// <returns>The type of the stolen value.</returns>
    /// <remarks>
    /// Stealable types:
    /// - Raw entities (direct entity references)
    ///
    /// Non-stealable types (build error):
    /// - Viewed[T]    (read-only wrapper, scope-bound)
    /// - Grasped[T]  (exclusive wrapper, scope-bound)
    /// - Inspected[T] (thread-safe read wrapper, scope-bound)
    /// - Claimed[T]    (thread-safe exclusive wrapper, scope-bound)
    /// - Retained[T]  (shared-ownership wrapper)
    /// - Tracked[T]   (reference-counted wrapper)
    /// - Shared[T, P] (shared-ownership wrapper)
    /// - Marked[T, P] (reference-counted wrapper)
    /// - Hijacked[T]  (internal ownership wrapper)
    /// </remarks>
    private TypeSymbol AnalyzeStealExpression(StealExpression steal)
    {
        // Steal has side effects (deadref marking) and emits diagnostics. Overload-resolution
        // pre-passes can re-analyze argument expressions, which would re-emit errors and
        // double-process the steal. Cache the resolved type on the node so repeated analysis
        // of the same StealExpression is a no-op.
        if (steal.ResolvedType != null)
        {
            return steal.ResolvedType;
        }

        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: steal.Operand);

        // Check if the type is a scope-bound wrapper (cannot be stolen)
        if (IsMemoryToken(type: operandType))
        {
            string tokenKind = GetMemoryTokenKind(type: operandType);
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message: $"Cannot steal '{tokenKind}' - scope-bound wrappers cannot be stolen. " +
                         $"Only raw entities can be stolen.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // Check for Hijacked[T] (internal ownership, not for user code)
        if (IsHijacked(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealHijacked,
                message: "Cannot steal 'Hijacked[T]' - internal ownership type cannot be stolen.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // Owned[T] is explicitly stealable — ownership transfer is its design purpose
        bool isOwned = operandType is WrapperTypeInfo { Name: "Owned" };

        if (!isOwned && !IsRawEntityType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message: $"Cannot steal '{operandType.Name}' - only raw entities and Owned[T] can be stolen.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // #11: Deadref tracking — mark the stolen variable as invalidated
        if (steal.Operand is IdentifierExpression stolenId)
        {
            _deadrefVariables.Add(item: stolenId.Name);
        }

        // Return the same type (raw entity or Owned[T]); steal transfers ownership
        steal.ResolvedType = operandType;
        return operandType;
    }

    /// <summary>
    /// Checks if a type is a scope-bound wrapper (Viewed, Grasped).
    /// Scope-bound wrappers cannot be stolen.
    /// </summary>
    private static bool IsMemoryToken(TypeSymbol type)
    {
        return type.Name is "Viewed" or "Grasped";
    }

    /// <summary>
    /// Gets the kind of scope-bound wrapper for error messages.
    /// </summary>
    private static string GetMemoryTokenKind(TypeSymbol type)
    {
        if (type.Name.StartsWith(value: "Viewed"))
        {
            return "Viewed[T]";
        }

        if (type.Name.StartsWith(value: "Grasped"))
        {
            return "Grasped[T]";
        }

        return type.Name;
    }

    /// <summary>
    /// Checks if a type is Hijacked[T] (internal ownership type).
    /// </summary>
    private static bool IsHijacked(TypeSymbol type)
    {
        return type.Name == "Hijacked";
    }

    /// <summary>
    /// Analyzes a backindex expression (^n = index from end).
    /// Validates that the operand is a non-negative integer type.
    /// </summary>
    /// <param name="back">The back index expression to analyze.</param>
    /// <returns>The BackIndex type.</returns>
    /// <remarks>
    /// BackIndex expressions create indices that count from the end of a sequence:
    /// - ^1 = last element
    /// - ^2 = second to last element
    /// - ^0 = one past the end (valid for slicing, not indexing)
    ///
    /// Used with IndexExpression for end-relative indexing: list[^1], text[^3]
    /// </remarks>
    private TypeSymbol AnalyzeBackIndexExpression(BackIndexExpression back)
    {
        // BackIndex stores its offset as U64; retype untyped integer literals (`^1`) accordingly.
        TypeSymbol? u64Type = _registry.LookupType(name: "U64");
        TypeSymbol operandType =
            AnalyzeExpression(expression: back.Operand, expectedType: u64Type);

        if (!IsIntegerType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexRequiresInteger,
                message:
                $"BackIndex operator '^' requires an integer operand, got '{operandType.Name}'.",
                location: back.Location);
        }

        TypeSymbol? backIndexType = _registry.LookupType(name: "BackIndex");
        if (backIndexType != null)
        {
            return backIndexType;
        }

        // Fallback: return Address as the index representation
        return _registry.LookupType(name: "Address") ?? operandType;
    }

    /// <summary>
    /// Creates a RoutineTypeInfo from a RoutineInfo for first-class routine references.
    /// </summary>
    /// <param name="routine">The routine to create a type for.</param>
    /// <returns>The routine type representing this routine's signature.</returns>
    private RoutineTypeInfo GetRoutineType(RoutineInfo routine)
    {
        // Extract parameter types from ParameterInfo
        var parameterTypes = routine.Parameters
                                    .Select(selector: p => p.Type)
                                    .ToList();

        // Get return type (null means Blank/void)
        TypeSymbol? returnType = routine.ReturnType;

        // Create or retrieve the cached routine type
        return _registry.GetOrCreateRoutineType(parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: routine.IsFailable);
    }
}
