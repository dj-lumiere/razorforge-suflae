using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
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
/// Phase 2.6: Generates derived comparison operators from eq and cmp routines,
/// and synthesizes crash_title() bodies for all crashable types.
/// </summary>
internal sealed class DerivedOperatorPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;
    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    public DerivedOperatorPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        List<SemanticError> errors)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
    }

    /// <summary>
    /// Generates derived comparison operators from eq and cmp routines.
    /// </summary>
    public void Run()
    {
        foreach (TypeSymbol type in _registry.GetTypesWithMemberRoutines())
        {
            GenerateDerivedOperatorsForType(type: type);
        }

        // Synthesize crash_title() bodies for all crashable types
        foreach (TypeSymbol type in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            RoutineInfo? titleMemberRoutine = _registry.GetMemberRoutinesForType(type: type)
                                               .FirstOrDefault(predicate: m => m.Name == "crash_title");
            if (titleMemberRoutine == null || !titleMemberRoutine.IsSynthesized)
                continue;

            string title = CrashableTypeInfo.SynthesizeCrashTitle(typeName: type.Name);
            var titleBody = new ReturnStatement(
                Value: new LiteralExpression(Value: title,
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc),
                Location: _synthLoc);

            _synthesizedBodies[key: titleMemberRoutine.RegistryKey] = (titleMemberRoutine, titleBody);
        }
    }

    /// <summary>
    /// Generates derived operators for a specific type.
    /// </summary>
    private void GenerateDerivedOperatorsForType(TypeSymbol type)
    {
        IEnumerable<RoutineInfo> memberRoutines = _registry.GetMemberRoutinesForType(type: type);
        var memberRoutineList = memberRoutines.ToList();

        // Look for eq memberRoutine
        RoutineInfo? eqMemberRoutine = memberRoutineList.FirstOrDefault(predicate: m => m.Name == "eq");
        if (eqMemberRoutine != null)
        {
            GenerateNeFromEq(type: type, eqMemberRoutine: eqMemberRoutine, existingMemberRoutines: memberRoutineList);
        }

        // Look for cmp memberRoutine
        RoutineInfo? cmpMemberRoutine = memberRoutineList.FirstOrDefault(predicate: m => m.Name == "cmp");
        if (cmpMemberRoutine != null)
        {
            GenerateComparisonOperatorsFromCmp(type: type,
                cmpMemberRoutine: cmpMemberRoutine,
                existingMemberRoutines: memberRoutineList);
        }

        // Look for contains memberRoutine
        RoutineInfo? containsMemberRoutine =
            memberRoutineList.FirstOrDefault(predicate: m => m.Name == "contains");
        if (containsMemberRoutine != null)
        {
            GenerateNotContainsFromContains(type: type,
                containsMemberRoutine: containsMemberRoutine,
                existingMemberRoutines: memberRoutineList);
        }

    }

    /// <summary>
    /// Generates ne from eq.
    /// ne(you) = not me.eq(you: you)
    /// </summary>
    private void GenerateNeFromEq(TypeSymbol type, RoutineInfo eqMemberRoutine,
        List<RoutineInfo> existingMemberRoutines)
    {
        RoutineInfo? existingNe = existingMemberRoutines.FirstOrDefault(predicate: m => m.Name == "ne");

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

        var neMemberRoutine = new RoutineInfo(name: "ne")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = eqMemberRoutine.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            // Inherit `eq`'s generic-parameter constraints so `ne` is only available
            // for the same instantiations as `eq`. Without this, Array[T,N].ne is
            // unconditionally derivable even when eq requires `T obeys Equatable`,
            // and the synthesized `ne` body references a non-existent `eq` at link
            // time for instantiations that fail the constraint (e.g. Array[X,N]).
            GenericParameters = eqMemberRoutine.GenericParameters,
            GenericConstraints = eqMemberRoutine.GenericConstraints,
            Visibility = eqMemberRoutine.Visibility,
            Location = eqMemberRoutine.Location,
            Module = eqMemberRoutine.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: neMemberRoutine);

        // Build AST body: return not me.eq(you: you)
        string paramName = eqMemberRoutine.Parameters.Count > 0
            ? eqMemberRoutine.Parameters[index: 0].Name
            : "you";
        var neBody = BuildNegatedDelegateBody(
            ownerType: type,
            delegateMemberRoutine: eqMemberRoutine,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: neMemberRoutine.RegistryKey] = (neMemberRoutine, neBody);
    }

    /// <summary>
    /// Generates notcontains from contains.
    /// notcontains(item) = not me.contains(item: item)
    /// </summary>
    private void GenerateNotContainsFromContains(TypeSymbol type, RoutineInfo containsMemberRoutine,
        List<RoutineInfo> existingMemberRoutines)
    {
        RoutineInfo? existingNotContains =
            existingMemberRoutines.FirstOrDefault(predicate: m => m.Name == "notcontains");

        if (existingNotContains != null)
        {
            return;
        }

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var notContainsMemberRoutine = new RoutineInfo(name: "notcontains")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = containsMemberRoutine.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            // Inherit `contains`'s constraints so `notcontains` is only available for
            // the same instantiations.
            GenericParameters = containsMemberRoutine.GenericParameters,
            GenericConstraints = containsMemberRoutine.GenericConstraints,
            Visibility = containsMemberRoutine.Visibility,
            Location = containsMemberRoutine.Location,
            Module = containsMemberRoutine.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: notContainsMemberRoutine);

        // Build AST body: return not me.contains(item: item)
        string paramName = containsMemberRoutine.Parameters.Count > 0
            ? containsMemberRoutine.Parameters[index: 0].Name
            : "item";
        var notContainsBody = BuildNegatedDelegateBody(
            ownerType: type,
            delegateMemberRoutine: containsMemberRoutine,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: notContainsMemberRoutine.RegistryKey] = (notContainsMemberRoutine, notContainsBody);
    }

    /// <summary>
    /// Generates lt, le, gt, ge from cmp.
    /// lt(you) = me.cmp(you: you) == ComparisonSign.ME_SMALL
    /// le(you) = me.cmp(you: you) != ComparisonSign.ME_LARGE
    /// gt(you) = me.cmp(you: you) == ComparisonSign.ME_LARGE
    /// ge(you) = me.cmp(you: you) != ComparisonSign.ME_SMALL
    /// </summary>
    private void GenerateComparisonOperatorsFromCmp(TypeSymbol type, RoutineInfo cmpMemberRoutine,
        List<RoutineInfo> existingMemberRoutines)
    {
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        string cmpParamName = cmpMemberRoutine.Parameters.Count > 0
            ? cmpMemberRoutine.Parameters[index: 0].Name
            : "you";

        // (opName, caseName, equal-or-notequal)
        (string OpName, string CaseName, bool UseEqual)[] derivedOps =
        [
            ("lt", "ME_SMALL", true),
            ("le", "ME_LARGE", false),
            ("gt", "ME_LARGE", true),
            ("ge", "ME_SMALL", false)
        ];

        foreach ((string opName, string caseName, bool useEqual) in derivedOps)
        {
            RoutineInfo? existing =
                existingMemberRoutines.FirstOrDefault(predicate: m => m.Name == opName);

            if (existing != null)
            {
                // User provided their own implementation — it takes priority over generated.
                continue;
            }

            var derivedMemberRoutine = new RoutineInfo(name: opName)
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = type,
                Parameters = cmpMemberRoutine.Parameters,
                ReturnType = boolType,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                // Inherit `cmp`'s constraints so `lt/le/gt/ge` are only available for
                // the same instantiations.
                GenericParameters = cmpMemberRoutine.GenericParameters,
                GenericConstraints = cmpMemberRoutine.GenericConstraints,
                Visibility = cmpMemberRoutine.Visibility,
                Location = cmpMemberRoutine.Location,
                Module = cmpMemberRoutine.Module,
                Annotations = ["readonly"],
                IsSynthesized = true
            };

            _registry.RegisterRoutine(routine: derivedMemberRoutine);

            // Build AST body: return me.cmp(you: you) == ComparisonSign.ME_SMALL  (or != ME_LARGE etc.)
            var cmpBody = BuildCmpDerivedBody(
                ownerType: type,
                cmpMemberRoutine: cmpMemberRoutine,
                boolType: boolType,
                cmpParamName: cmpParamName,
                caseName: caseName,
                useEqual: useEqual);
            _synthesizedBodies[key: derivedMemberRoutine.RegistryKey] = (derivedMemberRoutine, cmpBody);
        }
    }

    /// <summary>
    /// Builds: return not me.{memberRoutineName}({paramName}: {paramName})
    /// </summary>
    private static BlockStatement BuildNegatedDelegateBody(TypeSymbol ownerType, RoutineInfo delegateMemberRoutine,
        TypeSymbol boolType, string paramName)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var call = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                MemberName: delegateMemberRoutine.Name,
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
            ResolvedRoutine = delegateMemberRoutine,
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
    /// Builds: return me.cmp({paramName}: {paramName}) == ComparisonSign.{caseName}
    /// or:     return me.cmp({paramName}: {paramName}) != ComparisonSign.{caseName}
    /// </summary>
    private Statement BuildCmpDerivedBody(TypeSymbol ownerType, RoutineInfo cmpMemberRoutine,
        TypeSymbol boolType, string cmpParamName, string caseName, bool useEqual)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var cmpCall = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                MemberName: "cmp",
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
            ResolvedRoutine = cmpMemberRoutine,
            ResolvedType = cmpMemberRoutine.ReturnType
        };

        TypeSymbol cmpResultType = cmpMemberRoutine.ReturnType ?? ErrorTypeInfo.Instance;

        // Use an integer literal for the ComparisonSign case value to avoid requiring
        // identifier resolution of 'ComparisonSign' in synthesized bodies.
        // ME_SMALL = -1 (receiver less than other), ME_LARGE = 1 (receiver greater).
        long caseIntValue = caseName == "ME_SMALL" ? -1L : 1L;
        var caseLiteral = new LiteralExpression(
            Value: caseIntValue,
            LiteralType: TokenType.S32Literal,
            Location: _synthLoc) { ResolvedType = cmpResultType };

        // Always use eq (guaranteed registered by AutoWiredRegistrationPass before DerivedOperatorPass).
        // ne may not yet be registered when this body is built (ordering not guaranteed).
        RoutineInfo? eqMemberRoutine = _registry.LookupMemberRoutine(type: cmpResultType, memberRoutineName: "eq");
        var eqCall = new CallExpression(
            Callee: new MemberExpression(Object: cmpCall, MemberName: "eq", Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(Name: "you", Value: caseLiteral, Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            ResolvedRoutine = eqMemberRoutine,
            ResolvedType = boolType
        };

        if (useEqual)
        {
            return new ReturnStatement(Value: eqCall, Location: _synthLoc);
        }

        // "not equal" case (le, ge): use if-return to avoid needing ne on ComparisonSign.
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
