namespace SemanticAnalysis;

using Enums;
using Symbols;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase 2: Wired routine synthesis and derived operator generation.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    /// <summary>
    /// Known wired methods that are valid operator/special methods.
    /// </summary>
    private static readonly HashSet<string> KnownWiredMethods =
    [
        // Creator
        "$create",

        // Arithmetic operators
        "$add", "$sub", "$mul", "$truediv", "$floordiv", "$mod", "$pow",

        // Wrapping arithmetic
        "$add_wrap", "$sub_wrap", "$mul_wrap", "$pow_wrap",

        // Clamping arithmetic
        "$add_clamp", "$sub_clamp", "$mul_clamp", "$truediv_clamp", "$pow_clamp",

        // Comparison operators
        "$eq", "$ne", "$lt", "$le", "$gt", "$ge", "$cmp",

        // Bitwise operators
        "$bitand", "$bitor", "$bitxor",
        "$ashl", "$ashr", "$lshl", "$lshr",

        // Unary operators
        "$neg", "$bitnot",

        // Unwrap operators
        "$unwrap", "$unwrap_or",

        // Membership operators
        "$contains", "$notcontains",

        // Iteration
        "$iter", "$next",

        // Indexing
        "$getitem", "$setitem",

        // Context management
        "$enter", "$exit",

        // Destructor/cleanup
        "$destroy",

        // In-place compound assignment operators
        "$iadd", "$isub", "$imul", "$itruediv", "$ifloordiv", "$imod",
        "$ipow",
        "$ibitand", "$ibitor", "$ibitxor",
        "$iashl", "$iashr", "$ilshl", "$ilshr"
    ];

    /// <summary>
    /// Checks if a routine name uses the $ prefix but is not a known built-in method.
    /// </summary>
    private static bool IsUnknownWiredMethod(string name)
    {
        if (!name.StartsWith(value: "$", comparisonType: StringComparison.Ordinal) ||
            name.Length <= 1)
        {
            return false;
        }

        return !KnownWiredMethods.Contains(value: name);
    }

    /// <summary>
    /// Maps operator wired methods to their required protocols.
    /// Types must follow the protocol to define the operator method.
    /// </summary>
    private static readonly Dictionary<string, string[]> WiredToProtocols = new()
    {
        // Arithmetic operators
        [key: "$add"] = ["Addable", "DurationAddable"],
        [key: "$sub"] = ["Subtractable", "DurationSubtractable"],
        [key: "$mul"] = ["Multiplicable", "TextRepeatable", "Scalable"],
        [key: "$truediv"] = ["Divisible", "ScalarDivisible"],
        [key: "$floordiv"] = ["FloorDivisible", "ScalarFloorDivisible"],
        [key: "$mod"] = ["FloorDivisible"],
        [key: "$pow"] = ["Exponentiable"],

        // Wrapping arithmetic
        [key: "$add_wrap"] = ["WrappingAddable"],
        [key: "$sub_wrap"] = ["WrappingSubtractable"],
        [key: "$mul_wrap"] = ["WrappingMultiplicable"],
        [key: "$pow_wrap"] = ["WrappingExponentiable"],

        // Clamping arithmetic
        [key: "$add_clamp"] = ["ClampingAddable"],
        [key: "$sub_clamp"] = ["ClampingSubtractable"],
        [key: "$mul_clamp"] = ["ClampingMultiplicable"],
        [key: "$truediv_clamp"] = ["ClampingDivisible"],
        [key: "$pow_clamp"] = ["ClampingExponentiable"],

        // Comparison operators
        [key: "$eq"] = ["Equatable"],
        [key: "$ne"] = ["Equatable"],
        [key: "$cmp"] = ["Comparable"],
        [key: "$lt"] = ["Comparable"],
        [key: "$le"] = ["Comparable"],
        [key: "$gt"] = ["Comparable"],
        [key: "$ge"] = ["Comparable"],

        // Bitwise operators
        [key: "$bitand"] = ["Bitwiseable"],
        [key: "$bitor"] = ["Bitwiseable"],
        [key: "$bitxor"] = ["Bitwiseable"],

        // Shift operators
        [key: "$ashl"] = ["Shiftable"],
        [key: "$ashr"] = ["Shiftable"],
        [key: "$lshl"] = ["Shiftable"],
        [key: "$lshr"] = ["Shiftable"],
        // Unary operators
        [key: "$neg"] = ["Negatable"],
        [key: "$bitnot"] = ["Invertible"],

        // Container operators
        [key: "$contains"] = ["Container"],
        [key: "$notcontains"] = ["Container"],
        [key: "$getitem"] = ["Indexable"],
        [key: "$setitem"] = ["Indexable"],

        // Sequence operators
        [key: "$iter"] = ["Iterable"],
        [key: "$next"] = ["Iterator"],

        // In-place compound assignment operators
        [key: "$iadd"] = ["InPlaceAddable"],
        [key: "$isub"] = ["InPlaceSubtractable"],
        [key: "$imul"] = ["InPlaceMultiplicable"],
        [key: "$itruediv"] = ["InPlaceDivisible"],
        [key: "$ifloordiv"] = ["InPlaceFloorDivisible"],
        [key: "$imod"] = ["InPlaceFloorDivisible"],
        [key: "$ipow"] = ["InPlaceExponentiable"],
        [key: "$ibitand"] = ["InPlaceBitwiseable"],
        [key: "$ibitor"] = ["InPlaceBitwiseable"],
        [key: "$ibitxor"] = ["InPlaceBitwiseable"],
        [key: "$iashl"] = ["InPlaceShiftable"],
        [key: "$iashr"] = ["InPlaceShiftable"],
        [key: "$ilshl"] = ["InPlaceShiftable"],
        [key: "$ilshr"] = ["InPlaceShiftable"]
    };

    /// <summary>
    /// Gets the required protocol for a wired method, or null if no protocol is required.
    /// </summary>
    private static IReadOnlyList<string>? GetRequiredProtocols(string wiredName)
    {
        return WiredToProtocols.GetValueOrDefault(key: wiredName);
    }

    private void CollectExternalDeclaration(ExternalDeclaration external)
    {
        // #123: Suflae cannot use C interop directly
        if (_registry.Language == Language.Suflae)
        {
            ReportError(code: SemanticDiagnosticCode.SuflaeNoCInterop,
                message:
                $"Suflae does not support C interop. External declaration '{external.Name}' is not allowed. " +
                "Use RazorForge for native interop.",
                location: external.Location);
        }

        var routineInfo = new RoutineInfo(name: external.Name)
        {
            Kind = RoutineKind.External,
            CallingConvention = external.CallingConvention,
            IsVariadic = external.IsVariadic,
            Visibility = VisibilityModifier.Open, // External declarations are always open
            Location = external.Location,
            Module = GetCurrentModuleName(),
            Annotations = external.Annotations ?? [],
            IsDangerous = external.IsDangerous,
            GenericParameters = external.GenericParameters,
            GenericConstraints = external.GenericConstraints
        };

        _registry.RegisterRoutine(routine: routineInfo);
    }

    private void TryRegisterType(TypeSymbol type, SourceLocation location)
    {
        try
        {
            _registry.RegisterType(type: type);
        }
        catch (InvalidOperationException)
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateTypeDefinition,
                message: $"Type '{type.Name}' is already defined.",
                location: location);
        }
    }

    #region Phase 2.6: Derived Operator Generation

    /// <summary>
    /// Generates derived comparison operators from $eq and $cmp routines.
    /// </summary>
    private void GenerateDerivedOperators()
    {
        foreach (TypeSymbol type in _registry.GetTypesWithMethods())
        {
            GenerateDerivedOperatorsForType(type: type);
        }
    }

    /// <summary>
    /// Generates derived operators for a specific type.
    /// </summary>
    /// <param name="type">The type to generate operators for.</param>
    private void GenerateDerivedOperatorsForType(TypeSymbol type)
    {
        IEnumerable<RoutineInfo> methods = _registry.GetMethodsForType(type: type);
        var methodList = methods.ToList();

        // Look for $eq method
        RoutineInfo? eqMethod = methodList.FirstOrDefault(predicate: m => m.Name == "$eq");
        if (eqMethod != null)
        {
            GenerateNeFromEq(type: type, eqMethod: eqMethod, existingMethods: methodList);
        }

        // Look for $cmp method
        RoutineInfo? cmpMethod = methodList.FirstOrDefault(predicate: m => m.Name == "$cmp");
        if (cmpMethod != null)
        {
            GenerateComparisonOperatorsFromCmp(type: type,
                cmpMethod: cmpMethod,
                existingMethods: methodList);
        }

        // Look for $contains method
        RoutineInfo? containsMethod =
            methodList.FirstOrDefault(predicate: m => m.Name == "$contains");
        if (containsMethod != null)
        {
            GenerateNotContainsFromContains(type: type,
                containsMethod: containsMethod,
                existingMethods: methodList);
        }
    }

    /// <summary>
    /// Generates $ne from $eq.
    /// $ne(you) = not $eq(you)
    /// </summary>
    private void GenerateNeFromEq(TypeSymbol type, RoutineInfo eqMethod,
        List<RoutineInfo> existingMethods)
    {
        RoutineInfo? existingNe = existingMethods.FirstOrDefault(predicate: m => m.Name == "$ne");

        if (existingNe != null)
        {
            // User provided their own implementation — it takes priority over generated.
            // This is expected behavior for @generated protocol routines (#179).
            return;
        }

        // Generate $ne
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return; // Bool type not available
        }

        var neMethod = new RoutineInfo(name: "$ne")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = eqMethod.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = eqMethod.Visibility,
            Location = eqMethod.Location,
            Module = eqMethod.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: neMethod);
    }

    /// <summary>
    /// Generates $notcontains from $contains.
    /// $notcontains(item) = not $contains(item)
    /// </summary>
    private void GenerateNotContainsFromContains(TypeSymbol type, RoutineInfo containsMethod,
        List<RoutineInfo> existingMethods)
    {
        RoutineInfo? existingNotContains =
            existingMethods.FirstOrDefault(predicate: m => m.Name == "$notcontains");

        if (existingNotContains != null)
        {
            return;
        }

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var notContainsMethod = new RoutineInfo(name: "$notcontains")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = containsMethod.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = containsMethod.Visibility,
            Location = containsMethod.Location,
            Module = containsMethod.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: notContainsMethod);
    }

    /// <summary>
    /// Generates $lt, $le, $gt, $ge from $cmp.
    /// $lt(you) = $cmp(you) is ME_SMALL
    /// $le(you) = $cmp(you) isnot ME_LARGE
    /// $gt(you) = $cmp(you) is ME_LARGE
    /// $ge(you) = $cmp(you) isnot ME_SMALL
    /// </summary>
    private void GenerateComparisonOperatorsFromCmp(TypeSymbol type, RoutineInfo cmpMethod,
        List<RoutineInfo> existingMethods)
    {
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return; // Bool type not available
        }

        // Define the derived operators
        (string, string, bool)[] derivedOps = new[]
        {
            ("$lt", "ME_SMALL", true), // is ME_SMALL
            ("$le", "ME_LARGE", false), // isnot ME_LARGE
            ("$gt", "ME_LARGE", true), // is ME_LARGE
            ("$ge", "ME_SMALL", false) // isnot ME_SMALL
        };

        foreach ((string opName, string _, bool _) in derivedOps)
        {
            RoutineInfo? existing =
                existingMethods.FirstOrDefault(predicate: m => m.Name == opName);

            if (existing != null)
            {
                // User provided their own implementation — it takes priority over generated.
                // This is expected behavior for @generated protocol routines (#179).
                continue;
            }

            // Generate the derived operator
            var derivedMethod = new RoutineInfo(name: opName)
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = type,
                Parameters = cmpMethod.Parameters,
                ReturnType = boolType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = cmpMethod.Visibility,
                Location = cmpMethod.Location,
                Module = cmpMethod.Module,
                Annotations = ["readonly"],
                IsSynthesized = true
            };

            _registry.RegisterRoutine(routine: derivedMethod);
        }
    }

    #endregion
}
