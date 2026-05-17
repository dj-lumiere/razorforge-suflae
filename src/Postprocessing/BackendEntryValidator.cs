using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Resolution;
using Verification.Results;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Postprocessing;

/// <summary>
/// Validates that programs crossing the backend boundary no longer contain AST shapes that
/// earlier phases have committed to eliminate before code generation.
/// </summary>
public sealed class BackendEntryValidator
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildPropertyCache = new();
    /// <summary>
    /// Stores the registry state used by this compiler phase.
    /// </summary>
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a validator for the final postprocessing-to-codegen boundary.
    /// </summary>
    public BackendEntryValidator(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Validates a full program and returns any residual-node errors.
    /// </summary>
    public IReadOnlyList<SemanticError> ValidateProgram(Program program)
    {
        return ValidateNode(node: program, registry: _registry);
    }

    /// <summary>
    /// Validates a synthesized statement body and returns any residual-node errors.
    /// </summary>
    public IReadOnlyList<SemanticError> ValidateStatement(Statement statement)
    {
        return ValidateNode(node: statement, registry: _registry);
    }

    /// <summary>
    /// Validates a concrete monomorphized body before backend entry.
    /// </summary>
    public static IReadOnlyList<SemanticError> ValidateMonomorphizedBody(MonomorphizedBody body)
    {
        var errors = new List<SemanticError>();

        // Routines whose owner is still a generic parameter are scaffolding for generic bodies
        // that get fully monomorphized at the call site -> they are never emitted standalone.
        if (body.Info.OwnerType is GenericParameterTypeInfo)
        {
            return errors;
        }

        // @innate routines are compiler-intrinsic stubs (e.g. `T.type_name`, `T.var_name`,
        // `page_size`). They are declared without a body in source — codegen and SA already
        // skip them. Monomorphizations inherit the empty body legitimately, so they must not
        // trigger MissingMonomorphizedBody.
        if (body.Info.Annotations.Contains(value: "innate"))
        {
            return errors;
        }

        if (!body.IsSynthesized &&
            !body.Info.IsSynthesized &&
            body.Ast.Body is BlockStatement { Statements.Count: 0 })
        {
            errors.Add(item: new SemanticError(
                Code: SemanticDiagnosticCode.MissingMonomorphizedBody,
                Message:
                $"{body.Info.RegistryKey} is a concrete monomorphized routine but has no concrete AST body. Phase 6 must produce the final body before backend entry.",
                Location: body.Info.Location ?? body.Ast.Location));
        }

        return errors;
    }

    /// <summary>
    /// Validates one syntax tree root and all descendants against backend-entry invariants.
    /// </summary>
    private static List<SemanticError> ValidateNode(ISyntaxTreeNode node, TypeRegistry registry)
    {
        var errors = new List<SemanticError>();
        Walk(node: node, errors: errors, registry: registry);
        return errors;
    }

    /// <summary>
    /// Traverses the tree, collecting every residual-node error instead of failing fast.
    /// </summary>
    private static void Walk(ISyntaxTreeNode node, List<SemanticError> errors, TypeRegistry registry)
    {
        // Skip bodies of generic routine declarations: their expressions legitimately carry
        // GenericParameterTypeInfo in ResolvedType (e.g. `me.tree[i]` on `me.tree: List[V]`
        // is typed V in the gen-def template). The monomorphized clones are validated
        // separately in the _instantiatedGenericBodies loop, so we don't lose coverage.
        if (node is RoutineDeclaration { GenericParameters: { Count: > 0 } })
            return;

        if (TryCreateResidualError(node: node, registry: registry, out SemanticError? error))
            errors.Add(item: error!);

        foreach (ISyntaxTreeNode child in EnumerateChildren(node: node))
            Walk(node: child, errors: errors, registry: registry);
    }

    /// <summary>
    /// Creates an error when a node still requires semantic metadata or a lowering pass before codegen.
    /// </summary>
    private static bool TryCreateResidualError(ISyntaxTreeNode node, TypeRegistry registry,
        out SemanticError? error)
    {
        if (node is IdentifierExpression identifier &&
            registry.LookupVariable(name: identifier.Name) is { IsPreset: true })
        {
            error = new SemanticError(
                Code: SemanticDiagnosticCode.IllegalBackendPresetIdentifier,
                Message:
                $"Preset identifier '{identifier.Name}' survived backend entry. PresetInliningPass must inline it before code generation.",
                Location: identifier.Location);
            return true;
        }

        if (node is CallExpression
            {
                Callee: IdentifierExpression callee,
                LoweringKind: CallLoweringKind.Unknown,
                ConstructedType: null,
                ResolvedRoutine: null
            } constructorLikeCall &&
            registry.LookupType(name: callee.Name) != null)
        {
            error = new SemanticError(
                Code: SemanticDiagnosticCode.MissingCallLoweringMetadata,
                Message:
                $"Constructor-like call '{callee.Name}(...)' reached backend entry without semantic lowering metadata. " +
                "Semantic analysis must classify it as a constructor/conversion and attach ConstructedType before code generation.",
                Location: constructorLikeCall.Location);
            return true;
        }

        if (node is CallExpression
            {
                Callee: IdentifierExpression routineCallee,
                ResolvedRoutine: null,
                ConstructedType: null,
                ResolvedType: null
            } unresolvedFreeCall &&
            registry.LookupRoutine(fullName: routineCallee.Name.EndsWith(value: '!')
                    ? routineCallee.Name[..^1]
                    : routineCallee.Name) != null)
        {
            error = new SemanticError(
                Code: SemanticDiagnosticCode.MissingCallLoweringMetadata,
                Message:
                $"Direct routine call '{routineCallee.Name}(...)' reached backend entry without resolved routine or result type metadata. " +
                "Semantic analysis must attach concrete call metadata before code generation.",
                Location: unresolvedFreeCall.Location);
            return true;
        }

        if (node is IndexExpression { ResolvedType: { } indexType } indexExpression &&
            ContainsUnresolvedBackendGeneric(type: indexType))
        {
            error = new SemanticError(
                Code: SemanticDiagnosticCode.UnresolvedBackendGeneric,
                Message:
                $"IndexExpression reached backend entry with an unresolved generic result type '{indexType.Name}'. " +
                "Semantic analysis and instantiation must attach the final concrete element type before code generation.",
                Location: indexExpression.Location);
            return true;
        }

        if (node is Expression exprWithRepr &&
            exprWithRepr is not TypeExpression &&
            exprWithRepr.ResolvedType is { } reprResolvedType &&
            reprResolvedType is not ErrorTypeInfo &&
            exprWithRepr.ResolvedRepr == null)
        {
            error = new SemanticError(
                Code: SemanticDiagnosticCode.MissingBackendRepresentation,
                Message:
                $"{exprWithRepr.GetType().Name} has semantic type '{reprResolvedType.FullName}' but no backend representation. BackendRepresentationPass must classify it before backend entry.",
                Location: exprWithRepr.Location);
            return true;
        }

        // Async nodes still have an active backend/codegen path today; validate those in a later
        // hardening step after AsyncLoweringPass owns them.
        string? requiredPass = node switch
        {
            GenericMethodCallExpression => "GenericCallLoweringPass",
            LambdaExpression => "LambdaLiftingPass",
            InsertedTextExpression => "FStringLoweringPass",
            RangeExpression => "ExpressionLoweringPass",
            UsingStatement => "UsingLoweringPass",
            BecomesStatement => "BecomesLoweringPass",
            _ => null
        };

        if (requiredPass == null)
        {
            error = null;
            return false;
        }

        error = new SemanticError(
            Code: SemanticDiagnosticCode.IllegalBackendResidualNode,
            Message:
            $"{node.GetType().Name} survived postprocessing. {requiredPass} must eliminate it before backend entry.",
            Location: node.Location);
        return true;
    }

    /// <summary>
    /// Returns true when a type graph still contains generic placeholders that codegen cannot lay out.
    /// </summary>
    private static bool ContainsUnresolvedBackendGeneric(TypeModel.Types.TypeInfo type)
    {
        if (type is GenericParameterTypeInfo or ProtocolSelfTypeInfo or ErrorTypeInfo)
        {
            return true;
        }

        if (type.IsGenericDefinition && type.TypeArguments is not { Count: > 0 })
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } &&
            type.TypeArguments.Any(ContainsUnresolvedBackendGeneric))
        {
            return true;
        }

        return type switch
        {
            WrapperTypeInfo wrapper => ContainsUnresolvedBackendGeneric(type: wrapper.InnerType),
            TupleTypeInfo tuple => tuple.ElementTypes.Any(ContainsUnresolvedBackendGeneric),
            VariantTypeInfo variant => variant.Members.Any(member =>
                member.Type != null && ContainsUnresolvedBackendGeneric(type: member.Type)),
            _ => false
        };
    }

    /// <summary>
    /// Enumerates child syntax nodes through public record properties so validation follows new AST shapes by default.
    /// </summary>
    /// <remarks>
    /// Filters out known AST alias properties (<c>IfStatement.ThenBranch</c> / <c>ElseBranch</c>,
    /// <c>ReturnStatement.Expression</c>) so nested constructs aren't visited 2^depth times.
    /// </remarks>
    private static IEnumerable<ISyntaxTreeNode> EnumerateChildren(ISyntaxTreeNode node)
    {
        PropertyInfo[] properties = ChildPropertyCache.GetOrAdd(node.GetType(), static type =>
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(predicate: property =>
                    property.Name != nameof(ISyntaxTreeNode.Location) &&
                    property.CanRead &&
                    property.GetIndexParameters().Length == 0 &&
                    !IsAstAliasProperty(ownerType: type, propertyName: property.Name))
                .ToArray());

        foreach (PropertyInfo property in properties)
        {
            object? value = property.GetValue(obj: node);
            switch (value)
            {
                case null:
                case string:
                    continue;
                case ISyntaxTreeNode childNode:
                    yield return childNode;
                    continue;
                case IEnumerable sequence:
                    foreach (object? item in sequence)
                    {
                        if (item is ISyntaxTreeNode child)
                            yield return child;
                    }

                    continue;
            }
        }
    }

    private static bool IsAstAliasProperty(Type ownerType, string propertyName)
    {
        return (ownerType.Name == "IfStatement" && (propertyName == "ThenBranch" || propertyName == "ElseBranch"))
            || (ownerType.Name == "ReturnStatement" && propertyName == "Expression");
    }
}
