using Compiler.Lexer;
using Compiler.Resolution;
using Verification.Enums;
using Verification.Results;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
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
                    LiteralType: TokenType.TextLiteral,
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
            ownerType: type,
            delegateMethod: eqMethod,
            boolType: boolType,
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
            ownerType: type,
            delegateMethod: containsMethod,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: notContainsMethod.RegistryKey] = (notContainsMethod, notContainsBody);
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
                ownerType: type,
                cmpMethod: cmpMethod,
                boolType: boolType,
                cmpParamName: cmpParamName,
                caseName: caseName,
                useEqual: useEqual);
            _synthesizedBodies[key: derivedMethod.RegistryKey] = (derivedMethod, cmpBody);
        }
    }

    /// <summary>
    /// Builds: return not me.{methodName}({paramName}: {paramName})
    /// </summary>
    private static Statement BuildNegatedDelegateBody(TypeSymbol ownerType, RoutineInfo delegateMethod,
        TypeSymbol boolType, string paramName)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var call = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: delegateMethod.Name,
                Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(
                    Name: paramName,
                    Value: new IdentifierExpression(Name: paramName, Location: _synthLoc),
                    Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            ResolvedRoutine = delegateMethod,
            ResolvedType = boolType
        };

        var falseVal = new LiteralExpression(Value: false, LiteralType: TokenType.False,
            Location: _synthLoc) { ResolvedType = boolType };
        var trueVal = new LiteralExpression(Value: true, LiteralType: TokenType.True,
            Location: _synthLoc) { ResolvedType = boolType };
        // Codegen can't handle ConditionalExpression or UnaryNot on synthesized bodies
        // (ExpressionLoweringPass only runs on source AST). Use if-return instead.
        return new BlockStatement(
            Statements:
            [
                new IfStatement(
                    Condition: call,
                    ThenStatement: new ReturnStatement(Value: falseVal, Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc),
                new ReturnStatement(Value: trueVal, Location: _synthLoc)
            ],
            Location: _synthLoc);
    }

    /// <summary>
    /// Builds: return me.$cmp({paramName}: {paramName}) == ComparisonSign.{caseName}
    /// or:     return me.$cmp({paramName}: {paramName}) != ComparisonSign.{caseName}
    /// </summary>
    private Statement BuildCmpDerivedBody(TypeSymbol ownerType, RoutineInfo cmpMethod,
        TypeSymbol boolType, string cmpParamName, string caseName, bool useEqual)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var cmpCall = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: "$cmp",
                Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(
                    Name: cmpParamName,
                    Value: new IdentifierExpression(Name: cmpParamName, Location: _synthLoc),
                    Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            ResolvedRoutine = cmpMethod,
            ResolvedType = cmpMethod.ReturnType
        };

        TypeSymbol cmpResultType = cmpMethod.ReturnType ?? ErrorTypeInfo.Instance;

        // Use an integer literal for the ComparisonSign case value to avoid requiring
        // identifier resolution of 'ComparisonSign' in synthesized bodies.
        // ME_SMALL = -1 (receiver less than other), ME_LARGE = 1 (receiver greater).
        long caseIntValue = caseName == "ME_SMALL" ? -1L : 1L;
        var caseLiteral = new LiteralExpression(
            Value: caseIntValue,
            LiteralType: TokenType.S32Literal,
            Location: _synthLoc) { ResolvedType = cmpResultType };

        // Always use $eq (guaranteed registered by AutoWiredRegistrationPass before DerivedOperatorPass).
        // $ne may not yet be registered when this body is built (ordering not guaranteed).
        RoutineInfo? eqMethod = _registry.LookupMethod(type: cmpResultType, methodName: "$eq");
        var eqCall = new CallExpression(
            Callee: new MemberExpression(Object: cmpCall, PropertyName: "$eq", Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(Name: "you", Value: caseLiteral, Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            ResolvedRoutine = eqMethod,
            ResolvedType = boolType
        };

        if (useEqual)
        {
            return new ReturnStatement(Value: eqCall, Location: _synthLoc);
        }

        // "not equal" case ($le, $ge): use if-return to avoid needing $ne on ComparisonSign.
        var falseVal = new LiteralExpression(Value: false, LiteralType: TokenType.False,
            Location: _synthLoc) { ResolvedType = boolType };
        var trueVal = new LiteralExpression(Value: true, LiteralType: TokenType.True,
            Location: _synthLoc) { ResolvedType = boolType };
        return new BlockStatement(
            Statements:
            [
                new IfStatement(
                    Condition: eqCall,
                    ThenStatement: new ReturnStatement(Value: falseVal, Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc),
                new ReturnStatement(Value: trueVal, Location: _synthLoc)
            ],
            Location: _synthLoc);
    }
}
