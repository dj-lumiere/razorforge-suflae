namespace SemanticVerification;

using Enums;
using Symbols;
using SyntaxTree;
using Types;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase D synthesizer: lazily generates transparent-forwarding routines on wrapper
/// types (Owned[T], Viewed[T], Hijacked[T], etc.) when user code calls a method that
/// exists on the inner type T but not directly on the wrapper.
///
/// Synthesis anchors on the wrapper's generic definition (e.g. Owned[T]) so that
/// monomorphization handles per-instance specialization.  The forwarder body is:
///
///   danger!
///     var raw = Snatched[T](me)
///     return raw.read().method(arg1: arg1, ...)
///
/// where T is the wrapper's generic parameter.  When monomorphized with T→List[Byte],
/// the body's Snatched[T] becomes Snatched[List[Byte]], and expression types resolve
/// transitively through raw.read() to the concrete inner type.
///
/// Policy:
///   - Read-only wrappers (Viewed, Inspected) forward ONLY @readonly methods of T.
///   - All other wrappers forward any modification category.
///
/// Signature synthesis: params/return are taken from the inner method's signature
/// on the inner-generic-def type (e.g. List[T].$getitem!).  GMP's
/// BuildConcreteRoutineInfo performs name-based substitution at monomorphization
/// time.  For methods whose return depends on the inner's generic param (e.g.
/// List[T].$getitem! returning T), the forwarder is marked with
/// <see cref="RoutineInfo.WrapperForwarderInnerMethod"/> so GMP can re-resolve the
/// signature against the concrete inner type.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    /// <summary>Keyed by (wrapperDefName, methodName, isFailable) — caches synthesized forwarders.</summary>
    private readonly HashSet<string> _synthesizedForwarderKeys = [];

    /// <summary>
    /// Attempts to synthesize a forwarding routine on a wrapper type that delegates to
    /// a matching method on the wrapper's inner type T. Returns null if synthesis is
    /// not applicable (not a wrapper, no inner T, no matching inner method, or read-only
    /// wrapper rejecting a non-readonly inner method).
    /// </summary>
    private RoutineInfo? TrySynthesizeWrapperForwarder(TypeSymbol wrapperType,
        string methodName, bool isFailable)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        TypeSymbol? wrapperDef = wrapperType switch
        {
            RecordTypeInfo { GenericDefinition: { } def } => def,
            EntityTypeInfo { GenericDefinition: { } def } => def,
            WrapperTypeInfo => _registry.LookupType(name: wrapperType.Name),
            _ => wrapperType
        };

        if (wrapperDef == null || !wrapperDef.IsGenericDefinition ||
            wrapperDef.GenericParameters is not { Count: 1 })
        {
            return null;
        }

        string genericParamName = wrapperDef.GenericParameters[index: 0];

        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        TypeSymbol innerLookupType = innerType switch
        {
            RecordTypeInfo { GenericDefinition: { } d } => d,
            EntityTypeInfo { GenericDefinition: { } d } => d,
            _ => innerType
        };

        RoutineInfo? innerMethod =
            _registry.LookupMethod(type: innerLookupType, methodName: methodName,
                isFailable: isFailable);
        if (innerMethod == null)
        {
            return null;
        }

        if (IsReadOnlyWrapper(type: wrapperType) && !innerMethod.IsReadOnly)
        {
            return null;
        }

        string cacheKey = $"{wrapperDef.Name}.{methodName}#{(isFailable ? "!" : "")}";
        if (!_synthesizedForwarderKeys.Add(item: cacheKey))
        {
            return _registry.LookupMethod(type: wrapperType,
                methodName: methodName,
                isFailable: isFailable) ??
                _registry.LookupMethod(type: wrapperDef,
                    methodName: methodName,
                    isFailable: isFailable);
        }

        var forwarder = new RoutineInfo(name: innerMethod.Name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = wrapperDef,
            Parameters = innerMethod.Parameters,
            ReturnType = innerMethod.ReturnType,
            IsFailable = innerMethod.IsFailable,
            DeclaredModification = innerMethod.DeclaredModification,
            ModificationCategory = innerMethod.ModificationCategory,
            Visibility = innerMethod.Visibility,
            Location = innerMethod.Location,
            Module = innerMethod.Module,
            Annotations = innerMethod.Annotations,
            IsSynthesized = true,
            WrapperForwarderInnerMethod = innerMethod,
            WrapperForwarderInnerGenericDef = innerLookupType
        };

        Statement body = BuildWrapperForwarderBody(
            genericParamName: genericParamName,
            methodName: innerMethod.Name,
            isFailable: innerMethod.IsFailable,
            parameters: innerMethod.Parameters,
            hasReturnValue: innerMethod.ReturnType != null &&
                innerMethod.ReturnType.Name != "Blank");

        _registry.RegisterRoutine(routine: forwarder);
        _synthesizedBodies[key: forwarder.RegistryKey] = (forwarder, body);

        return _registry.LookupMethod(type: wrapperType,
            methodName: methodName,
            isFailable: isFailable) ?? forwarder;
    }

    /// <summary>
    /// Builds the AST body:
    ///   danger!
    ///     var raw = Snatched[T](me)
    ///     [return] raw.read().methodName(param1: param1, ...)
    /// where T is the wrapper's generic parameter name.
    /// </summary>
    private static Statement BuildWrapperForwarderBody(string genericParamName,
        string methodName, bool isFailable, IReadOnlyList<ParameterInfo> parameters,
        bool hasReturnValue)
    {
        var snatchedCall = new CreatorExpression(
            TypeName: "Snatched",
            TypeArguments:
            [
                new TypeExpression(Name: genericParamName, GenericArguments: null,
                    Location: _synthLoc)
            ],
            MemberVariables: [("", new IdentifierExpression(Name: "me", Location: _synthLoc))],
            Location: _synthLoc);

        var rawDecl = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: "raw",
                Type: null,
                Initializer: snatchedCall,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc);

        var readCall = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: "raw", Location: _synthLoc),
                PropertyName: "read",
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);

        string callPropertyName = isFailable ? methodName + "!" : methodName;
        var forwardedArgs = new List<Expression>();
        foreach (ParameterInfo p in parameters)
        {
            if (p.Name == "me")
            {
                continue;
            }
            forwardedArgs.Add(item: new NamedArgumentExpression(
                Name: p.Name,
                Value: new IdentifierExpression(Name: p.Name, Location: _synthLoc),
                Location: _synthLoc));
        }

        var innerCall = new CallExpression(
            Callee: new MemberExpression(
                Object: readCall,
                PropertyName: callPropertyName,
                Location: _synthLoc),
            Arguments: forwardedArgs,
            Location: _synthLoc);

        Statement callStmt = hasReturnValue
            ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
            : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);

        var innerBlock = new BlockStatement(
            Statements: [rawDecl, callStmt],
            Location: _synthLoc);

        return new DangerStatement(Body: innerBlock, Location: _synthLoc);
    }

}
