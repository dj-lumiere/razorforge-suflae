namespace SemanticVerification;

using Compiler.Synthesis;
using Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;
using Compiler.Diagnostics;
using TypeSymbol = TypeModel.Types.TypeInfo;

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

        // Display / text representation
        "$represent", "$diagnose",

        // Hashing and identity
        "$hash", "$same", "$notsame",

        // Copy
        "$copy",

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
        if (!name.StartsWith('$') || name.Length <= 1)
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
    internal static IReadOnlyList<string>? GetRequiredProtocols(string wiredName)
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
        new DerivedOperatorPass(_registry, _synthesizedBodies, _errors).Run();
    }

    #endregion
}
