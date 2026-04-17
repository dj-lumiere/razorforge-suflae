namespace SemanticVerification;

using Enums;
using Symbols;
using SyntaxTree;
using Compiler.Diagnostics;
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
        foreach (TypeSymbol type in _registry.GetTypesWithMethods())
        {
            GenerateDerivedOperatorsForType(type: type);
        }

        // Synthesize crash_title() bodies for all crashable types
        foreach (TypeSymbol type in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            RoutineInfo? titleMethod = _registry.GetMethodsForType(type: type)
                                               .FirstOrDefault(predicate: m => m.Name == "crash_title");
            if (titleMethod == null || !titleMethod.IsSynthesized)
                continue;

            string title = Types.CrashableTypeInfo.SynthesizeCrashTitle(typeName: type.Name);
            var titleBody = new ReturnStatement(
                Value: new LiteralExpression(Value: title,
                    LiteralType: Compiler.Lexer.TokenType.TextLiteral,
                    Location: _synthLoc),
                Location: _synthLoc);

            _synthesizedBodies[key: titleMethod.RegistryKey] = (titleMethod, titleBody);
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

        // Look for $same method ($notsame is @innate, always derived from $same)
        RoutineInfo? sameMethod = methodList.FirstOrDefault(predicate: m => m.Name == "$same");
        if (sameMethod != null)
        {
            GenerateNotSameFromSame(type: type, sameMethod: sameMethod);
        }
    }

    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    /// <summary>
    /// Generates $ne from $eq.
    /// $ne(you) = not me.$eq(you: you)
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

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
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

        // Build AST body: return not me.$eq(you: you)
        string paramName = eqMethod.Parameters.Count > 0
            ? eqMethod.Parameters[index: 0].Name
            : "you";
        var neBody = BuildNegatedDelegateBody(
            delegateMethodName: "$eq",
            paramName: paramName);
        _synthesizedBodies[key: neMethod.RegistryKey] = (neMethod, neBody);
    }

    /// <summary>
    /// Generates $notcontains from $contains.
    /// $notcontains(item) = not me.$contains(item: item)
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

        // Build AST body: return not me.$contains(item: item)
        string paramName = containsMethod.Parameters.Count > 0
            ? containsMethod.Parameters[index: 0].Name
            : "item";
        var notContainsBody = BuildNegatedDelegateBody(
            delegateMethodName: "$contains",
            paramName: paramName);
        _synthesizedBodies[key: notContainsMethod.RegistryKey] = (notContainsMethod, notContainsBody);
    }

    /// <summary>
    /// Generates $notsame from $same.
    /// $notsame(you) = not me.$same(you: you)
    /// $notsame is @innate — always derived, never user-overridden.
    /// </summary>
    private void GenerateNotSameFromSame(TypeSymbol type, RoutineInfo sameMethod)
    {
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var notSameMethod = new RoutineInfo(name: "$notsame")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = sameMethod.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = sameMethod.Visibility,
            Location = sameMethod.Location,
            Module = sameMethod.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: notSameMethod);

        string paramName = sameMethod.Parameters.Count > 0
            ? sameMethod.Parameters[index: 0].Name
            : "you";
        var notSameBody = BuildNegatedDelegateBody(
            delegateMethodName: "$same",
            paramName: paramName);
        _synthesizedBodies[key: notSameMethod.RegistryKey] = (notSameMethod, notSameBody);
    }

    /// <summary>
    /// Generates $lt, $le, $gt, $ge from $cmp.
    /// $lt(you) = me.$cmp(you: you) == ComparisonSign.ME_SMALL
    /// $le(you) = me.$cmp(you: you) != ComparisonSign.ME_LARGE
    /// $gt(you) = me.$cmp(you: you) == ComparisonSign.ME_LARGE
    /// $ge(you) = me.$cmp(you: you) != ComparisonSign.ME_SMALL
    /// </summary>
    private void GenerateComparisonOperatorsFromCmp(TypeSymbol type, RoutineInfo cmpMethod,
        List<RoutineInfo> existingMethods)
    {
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        string cmpParamName = cmpMethod.Parameters.Count > 0
            ? cmpMethod.Parameters[index: 0].Name
            : "you";

        // (opName, caseName, equal-or-notequal)
        (string OpName, string CaseName, bool UseEqual)[] derivedOps =
        [
            ("$lt", "ME_SMALL", true),
            ("$le", "ME_LARGE", false),
            ("$gt", "ME_LARGE", true),
            ("$ge", "ME_SMALL", false)
        ];

        foreach ((string opName, string caseName, bool useEqual) in derivedOps)
        {
            RoutineInfo? existing =
                existingMethods.FirstOrDefault(predicate: m => m.Name == opName);

            if (existing != null)
            {
                // User provided their own implementation — it takes priority over generated.
                continue;
            }

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

            // Build AST body: return me.$cmp(you: you) == ComparisonSign.ME_SMALL  (or != ME_LARGE etc.)
            var cmpBody = BuildCmpDerivedBody(
                cmpParamName: cmpParamName,
                caseName: caseName,
                useEqual: useEqual);
            _synthesizedBodies[key: derivedMethod.RegistryKey] = (derivedMethod, cmpBody);
        }
    }

    /// <summary>
    /// Builds: return not me.{methodName}({paramName}: {paramName})
    /// </summary>
    private static Statement BuildNegatedDelegateBody(string delegateMethodName, string paramName)
    {
        var call = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc),
                PropertyName: delegateMethodName,
                Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(
                    Name: paramName,
                    Value: new IdentifierExpression(Name: paramName, Location: _synthLoc),
                    Location: _synthLoc)
            ],
            Location: _synthLoc);

        return new ReturnStatement(
            Value: new UnaryExpression(
                Operator: UnaryOperator.Not,
                Operand: call,
                Location: _synthLoc),
            Location: _synthLoc);
    }

    /// <summary>
    /// Builds: return me.$cmp({paramName}: {paramName}) == ComparisonSign.{caseName}
    /// or:     return me.$cmp({paramName}: {paramName}) != ComparisonSign.{caseName}
    /// </summary>
    private static Statement BuildCmpDerivedBody(string cmpParamName, string caseName,
        bool useEqual)
    {
        var cmpCall = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc),
                PropertyName: "$cmp",
                Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(
                    Name: cmpParamName,
                    Value: new IdentifierExpression(Name: cmpParamName, Location: _synthLoc),
                    Location: _synthLoc)
            ],
            Location: _synthLoc);

        var caseRef = new MemberExpression(
            Object: new IdentifierExpression(Name: "ComparisonSign", Location: _synthLoc),
            PropertyName: caseName,
            Location: _synthLoc);

        return new ReturnStatement(
            Value: new BinaryExpression(
                Left: cmpCall,
                Operator: useEqual ? BinaryOperator.Equal : BinaryOperator.NotEqual,
                Right: caseRef,
                Location: _synthLoc),
            Location: _synthLoc);
    }

    #endregion
}
