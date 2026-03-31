namespace SemanticAnalysis;

using Enums;
using Results;
using Native;
using Symbols;
using Types;
using Compiler.Lexer;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private TypeSymbol AnalyzeInsertedTextExpression(InsertedTextExpression insertedText)
    {
        foreach (InsertedTextPart part in insertedText.Parts)
        {
            if (part is ExpressionPart exprPart)
            {
                // #16: F-text expression level restriction — only Level 3 expressions
                // (identifiers, literals, member access, calls) are allowed
                ValidateFTextExpression(expression: exprPart.Expression,
                    location: exprPart.Location);
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
    /// Validates that an f-text embedded expression is a Level 3 expression.
    /// Level 3: identifiers, literals, member access, routine calls, indexing.
    /// Disallowed: assignments, control flow, binary operators (except chained member access).
    /// </summary>
    private void ValidateFTextExpression(Expression expression, SourceLocation location)
    {
        switch (expression)
        {
            case IdentifierExpression:
            case LiteralExpression:
            case MemberExpression:
            case CallExpression:
            case IndexExpression:
            case OptionalMemberExpression:
                // Level 3 — allowed
                break;
            default:
                ReportError(code: SemanticDiagnosticCode.FTextExpressionLevelRestriction,
                    message:
                    "Only simple expressions (identifiers, literals, member access, calls) are allowed in f-text interpolation. " +
                    "Assign complex expressions to a variable first.",
                    location: location);
                break;
        }
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
    /// - Shared[T] (shared-ownership wrapper)
    /// - Tracked[T] (reference-counted wrapper)
    ///
    /// Non-stealable types (build error):
    /// - Viewed[T] (read-only wrapper, scope-bound)
    /// - Hijacked[T] (exclusive wrapper, scope-bound)
    /// - Inspected[T] (thread-safe read wrapper, scope-bound)
    /// - Seized[T] (thread-safe exclusive wrapper, scope-bound)
    /// - Snatched[T] (internal ownership, not for user code)
    /// </remarks>
    private TypeSymbol AnalyzeStealExpression(StealExpression steal)
    {
        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: steal.Operand);

        // Check if the type is a scope-bound wrapper (cannot be stolen)
        if (IsMemoryToken(type: operandType))
        {
            string tokenKind = GetMemoryTokenKind(type: operandType);
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message: $"Cannot steal '{tokenKind}' - scope-bound wrappers cannot be stolen. " +
                         $"Only raw entities, Shared[T], and Tracked[T] can be stolen.",
                location: steal.Location);
            return operandType;
        }

        // Check for Snatched[T] (internal ownership, not for user code)
        if (IsSnatched(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealSnatched,
                message: "Cannot steal 'Snatched[T]' - internal ownership type cannot be stolen.",
                location: steal.Location);
            return operandType;
        }

        // For Shared[T] or Tracked[T], return the inner type
        if (IsStealableHandle(type: operandType))
        {
            // Unwrap the wrapper to get the inner type
            if (operandType.TypeArguments is { Count: > 0 })
            {
                return operandType.TypeArguments[index: 0];
            }
        }

        // #11: Deadref tracking — mark the stolen variable as invalidated
        if (steal.Operand is IdentifierExpression stolenId)
        {
            _deadrefVariables.Add(item: stolenId.Name);
        }

        // For raw entities (not wrapped), return the same type
        // The steal operation moves ownership, making the source a deadref
        return operandType;
    }

    /// <summary>
    /// Checks if a type is a scope-bound wrapper (Viewed, Hijacked, Inspected, Seized).
    /// Scope-bound wrappers cannot be stolen.
    /// </summary>
    private static bool IsMemoryToken(TypeSymbol type)
    {
        return type.Name is "Viewed" or "Hijacked" or "Inspected" or "Seized" ||
               type.Name.StartsWith(value: "Viewed[") ||
               type.Name.StartsWith(value: "Hijacked[") ||
               type.Name.StartsWith(value: "Inspected[") || type.Name.StartsWith(value: "Seized[");
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

        if (type.Name.StartsWith(value: "Hijacked"))
        {
            return "Hijacked[T]";
        }

        if (type.Name.StartsWith(value: "Inspected"))
        {
            return "Inspected[T]";
        }

        if (type.Name.StartsWith(value: "Seized"))
        {
            return "Seized[T]";
        }

        return type.Name;
    }

    /// <summary>
    /// Checks if a type is Snatched[T] (internal ownership type).
    /// </summary>
    private static bool IsSnatched(TypeSymbol type)
    {
        return type.Name == "Snatched" || type.Name.StartsWith(value: "Snatched[");
    }

    /// <summary>
    /// Checks if a type is a stealable wrapper (Shared[T] or Tracked[T]).
    /// </summary>
    private static bool IsStealableHandle(TypeSymbol type)
    {
        return type.Name is "Shared" or "Tracked" || type.Name.StartsWith(value: "Shared[") ||
               type.Name.StartsWith(value: "Tracked[");
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
        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: back.Operand);

        // Validate that the operand is an integer type
        if (!IsIntegerType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexRequiresInteger,
                message:
                $"BackIndex operator '^' requires an integer operand, got '{operandType.Name}'.",
                location: back.Location);
        }

        // Return a BackIndex type (or Address as the underlying representation)
        // BackIndex is conceptually a wrapper around an offset from the end
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

    /// <summary>
    /// Analyzes a waitfor expression (suspended/threaded concurrency).
    /// Waits for a suspended or threaded operation to complete, optionally with a timeout.
    /// </summary>
    /// <param name="waitfor">The waitfor expression to analyze.</param>
    /// <returns>The type of the awaited value.</returns>
    private TypeSymbol AnalyzeWaitforExpression(WaitforExpression waitfor)
    {
        // Analyze the operand (the suspended or threaded operation to wait for)
        TypeSymbol operandType = AnalyzeExpression(expression: waitfor.Operand);

        // Analyze optional timeout expression
        if (waitfor.Timeout != null)
        {
            TypeSymbol timeoutType = AnalyzeExpression(expression: waitfor.Timeout);

            // Validate that timeout is a Duration type
            TypeSymbol? durationType = _registry.LookupType(name: "Duration");
            if (durationType != null && !IsAssignableTo(source: timeoutType, target: durationType))
            {
                ReportError(code: SemanticDiagnosticCode.WaitforTimeoutNotDuration,
                    message:
                    $"Waitfor 'within' clause requires a Duration, got '{timeoutType.Name}'.",
                    location: waitfor.Timeout.Location);
            }
        }

        // #14/#162: Validate that we're inside a suspended/threaded routine
        if (_currentRoutine != null && !_currentRoutine.IsAsync)
        {
            ReportError(code: SemanticDiagnosticCode.WaitforOutsideSuspendedRoutine,
                message:
                $"'waitfor' can only be used inside a 'suspended' or 'threaded' routine. " +
                $"Routine '{_currentRoutine.Name}' is neither suspended nor threaded.",
                location: waitfor.Location);
        }

        // The result type is the inner type of the suspended or threaded operation
        // For now, return the operand type directly
        return operandType;
    }

    /// <summary>
    /// Analyzes a dependent waitfor expression (task dependency graph).
    /// Syntax: after dep1 [as val1], dep2 [as val2] waitfor expr [within timeout]
    /// </summary>
    /// <param name="depWaitfor">The dependent waitfor expression to analyze.</param>
    /// <returns>Lookup&lt;T&gt; where T is the result type of the awaited operation.</returns>
    private TypeSymbol AnalyzeDependentWaitforExpression(DependentWaitforExpression depWaitfor)
    {
        // Create a new scope for the dependency bindings
        _registry.EnterScope(kind: ScopeKind.Block, name: "waitfor_deps");

        // Analyze each dependency
        foreach (TaskDependency dep in depWaitfor.Dependencies)
        {
            TypeSymbol depType = AnalyzeExpression(expression: dep.DependencyExpr);

            // Dependency must be Lookup<T> type
            if (depType is not ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Lookup } lookupType)
            {
                ReportError(code: SemanticDiagnosticCode.DependencyNotLookupType,
                    message: $"Task dependency must be a Lookup[T] type, got '{depType.Name}'.",
                    location: dep.Location);

                // If there's a binding, still declare it (as error type) to prevent cascading errors
                if (dep.BindingName != null)
                {
                    _registry.DeclareVariable(name: dep.BindingName, type: ErrorTypeInfo.Instance);
                }
            }
            else if (dep.BindingName != null)
            {
                // Introduce the binding variable with the unwrapped type T from Lookup<T>
                _registry.DeclareVariable(name: dep.BindingName, type: lookupType.ValueType);
            }
        }

        // Analyze the operand expression (with dependency bindings in scope)
        TypeSymbol operandType = AnalyzeExpression(expression: depWaitfor.Operand);

        // Analyze optional timeout expression
        if (depWaitfor.Timeout != null)
        {
            TypeSymbol timeoutType = AnalyzeExpression(expression: depWaitfor.Timeout);

            // Validate that timeout is a Duration type
            TypeSymbol? durationType = _registry.LookupType(name: "Duration");
            if (durationType != null && !IsAssignableTo(source: timeoutType, target: durationType))
            {
                ReportError(code: SemanticDiagnosticCode.WaitforTimeoutNotDuration,
                    message:
                    $"Waitfor 'within' clause requires a Duration, got '{timeoutType.Name}'.",
                    location: depWaitfor.Timeout.Location);
            }
        }

        // #15: Non-leaf waitfor (with dependencies) requires 'within' timeout clause
        if (depWaitfor.Timeout == null)
        {
            ReportError(code: SemanticDiagnosticCode.WaitforRequiresTimeout,
                message:
                "Dependent 'waitfor' (with 'after' clause) requires a 'within' timeout. " +
                "Add 'within <duration>' to prevent unbounded blocking on dependency chains.",
                location: depWaitfor.Location);
        }

        // Exit the dependency scope
        _registry.ExitScope();

        // #14/#162: Validate that we're inside a suspended/threaded routine
        if (_currentRoutine != null && !_currentRoutine.IsAsync)
        {
            ReportError(code: SemanticDiagnosticCode.WaitforOutsideSuspendedRoutine,
                message:
                $"'waitfor' can only be used inside a 'suspended' or 'threaded' routine. " +
                $"Routine '{_currentRoutine.Name}' is neither suspended nor threaded.",
                location: depWaitfor.Location);
        }

        // Result type is Lookup<R> where R is the operand type
        return ErrorHandlingTypeInfo.WellKnown.LookupDefinition.CreateInstance(typeArguments:
            [operandType]);
    }
}
