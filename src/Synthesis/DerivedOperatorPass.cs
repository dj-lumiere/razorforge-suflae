using Compiler.Resolution;
using TypeModel.Enums;
using SemanticVerification.Enums;
using SemanticVerification.Results;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;
using Compiler.Diagnostics;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Synthesis;

/// <summary>
/// Phase 2.6: Generates derived comparison operators from $eq and $cmp routines,
/// and synthesizes crash_title() bodies for all crashable types.
/// </summary>
internal sealed class DerivedOperatorPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;
    private readonly List<SemanticError> _errors;

    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    public DerivedOperatorPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        List<SemanticError> errors)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
        _errors = errors;
    }

    /// <summary>
    /// Generates derived comparison operators from $eq and $cmp routines.
    /// </summary>
    public void Run()
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

            string title = CrashableTypeInfo.SynthesizeCrashTitle(typeName: type.Name);
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
}
