using Compiler.Resolution;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Synthesis;

/// <summary>
/// Phase D synthesizer: lazily generates transparent-forwarding routines on wrapper
/// types (Owned[T], Viewed[T], Grasped[T], etc.) when user code calls a method that
/// exists on the inner type T but not directly on the wrapper.
///
/// Synthesis anchors on the wrapper's generic definition (e.g. Owned[T]) so that
/// monomorphization handles per-instance specialization.  The forwarder body is:
///
///   danger!
///     var raw = Hijacked[T](me)
///     return raw.read().method(arg1: arg1, ...)
///
/// where T is the wrapper's generic parameter.  When monomorphized with T→List[Byte],
/// the body's Hijacked[T] becomes Hijacked[List[Byte]], and expression types resolve
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
internal sealed class WrapperForwardingPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;
    private readonly HashSet<string> _synthesizedForwarderKeys;

    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    /// <summary>
    /// All wrapper types that transparently forward to their inner type.
    /// </summary>
    private static readonly HashSet<string> WrapperTypes =
    [
        "Viewed",    // Read-only single-threaded token
        "Grasped",   // Exclusive write single-threaded token
        "Inspected", // Read-only multi-threaded token
        "Claimed",   // Exclusive write multi-threaded token
        "Shared",    // Reference-counted multi-threaded handle
        "Marked",    // Weak reference multi-threaded handle
        "Retained",  // Reference-counted handle
        "Tracked",   // Weak reference handle
        "Hijacked",  // Unmanaged raw pointer handle
        "Owned"      // Exclusive ownership wrapper (unique_ptr equivalent)
    ];

    /// <summary>
    /// Read-only wrapper types that can only access @readonly methods.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyWrapperTypes =
    [
        "Viewed",    // Read-only single-threaded token
        "Inspected"  // Read-only multi-threaded token
    ];

    public WrapperForwardingPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        HashSet<string> synthesizedForwarderKeys)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
        _synthesizedForwarderKeys = synthesizedForwarderKeys;
    }

    /// <summary>
    /// Eagerly synthesizes forwarders on all concrete wrapper-type instantiations for every
    /// method found on their inner type.  Called after stdlib body analysis so that wrapper
    /// methods used only implicitly (e.g. release() via scope cleanup) are still forwarded.
    /// </summary>
    public void RunEager()
    {
        // Collect from both resolution caches: RecordTypeInfo resolutions AND WrapperTypeInfo resolutions.
        IEnumerable<TypeSymbol> candidates =
            _registry.AllConcreteGenericInstances
                     .Where(predicate: IsWrapperType)
                     .Cast<TypeSymbol>()
                     .Concat(_registry.AllConcreteWrapperInstances);

        foreach (TypeSymbol wrapperType in candidates)
        {
            TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
            if (innerType is null or GenericParameterTypeInfo)
                continue;

            TypeSymbol innerLookupType = innerType switch
            {
                RecordTypeInfo { GenericDefinition: { } d } => d,
                EntityTypeInfo { GenericDefinition: { } d } => d,
                _ => innerType
            };

            // Only eagerly synthesize forwarders for entity inner types.
            // Primitive/record inner types (Byte, U8, Character, etc.) have universal methods
            // whose codegen is not always valid for value types (e.g. get_address on i32).
            // Primitive wrappers get forwarders lazily when SA actually encounters a call site.
            if (innerLookupType is not EntityTypeInfo)
                continue;

            foreach (RoutineInfo innerMethod in _registry.GetMethodsForOwner(ownerType: innerLookupType))
            {
                TrySynthesize(wrapperType: wrapperType,
                    methodName: innerMethod.Name,
                    isFailable: innerMethod.IsFailable);
            }
        }
    }

    /// <summary>
    /// Attempts to synthesize a forwarding routine on a wrapper type that delegates to
    /// a matching method on the wrapper's inner type T. Returns null if synthesis is
    /// not applicable (not a wrapper, no inner T, no matching inner method, or read-only
    /// wrapper rejecting a non-readonly inner method).
    /// </summary>
    public RoutineInfo? TrySynthesize(TypeSymbol wrapperType, string methodName, bool isFailable)
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

        // RC record wrappers (Retained[T], Tracked[T]) are structs with a `data: Hijacked[T]`
        // field — cannot cast `me` to a pointer. Pointer wrappers use Hijacked[T](me) directly.
        // Detect by checking for an actual `data` member on the record def.
        string? dataFieldName = wrapperDef is RecordTypeInfo recDef &&
                                recDef.LookupMemberVariable(memberVariableName: "data") != null
            ? "data"
            : null;

        Statement body = BuildWrapperForwarderBody(
            genericParamName: genericParamName,
            methodName: innerMethod.Name,
            isFailable: innerMethod.IsFailable,
            parameters: innerMethod.Parameters,
            hasReturnValue: innerMethod.ReturnType != null &&
                innerMethod.ReturnType.Name != "Blank",
            dataFieldName: dataFieldName);

        _registry.RegisterRoutine(routine: forwarder);
        _synthesizedBodies[key: forwarder.RegistryKey] = (forwarder, body);

        return _registry.LookupMethod(type: wrapperType,
            methodName: methodName,
            isFailable: isFailable) ?? forwarder;
    }

    /// <summary>
    /// Builds the AST body:
    ///
    ///   Pointer wrappers (dataFieldName == null):
    ///     danger!
    ///       var raw = Hijacked[T](me)
    ///       [return] raw.read().methodName(param1: param1, ...)
    ///
    ///   Record-struct wrappers (dataFieldName == "data"):
    ///     danger!
    ///       [return] me.data.read().methodName(param1: param1, ...)
    ///
    /// where T is the wrapper's generic parameter name.
    /// </summary>
    private static Statement BuildWrapperForwarderBody(string genericParamName,
        string methodName, bool isFailable, IReadOnlyList<ParameterInfo> parameters,
        bool hasReturnValue, string? dataFieldName = null)
    {
        string callPropertyName = isFailable ? methodName + "!" : methodName;
        var forwardedArgs = new List<Expression>();
        foreach (ParameterInfo p in parameters)
        {
            if (p.Name == "me")
                continue;
            forwardedArgs.Add(item: new NamedArgumentExpression(
                Name: p.Name,
                Value: new IdentifierExpression(Name: p.Name, Location: _synthLoc),
                Location: _synthLoc));
        }

        List<Statement> innerStatements;

        if (dataFieldName != null)
        {
            // Record-struct wrapper: me.data.read().method(...)
            // Skip the `raw` variable entirely — no type inference needed.
            var dataAccess = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc),
                PropertyName: dataFieldName,
                Location: _synthLoc);
            var readCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: dataAccess,
                    PropertyName: "read",
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc);
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
            innerStatements = [callStmt];
        }
        else
        {
            // Pointer wrapper: var raw = Hijacked[T](me); raw.unhijack().method(...)
            // unhijack() reinterprets the ptr directly as T (no dereference) — correct for
            // Owned[T] where me IS the entity ptr, not a slot holding one.
            var hijackedCall = new CreatorExpression(
                TypeName: "Hijacked",
                TypeArguments:
                [
                    new TypeExpression(Name: genericParamName, GenericArguments: null,
                        Location: _synthLoc)
                ],
                MemberVariables:
                    [("", new IdentifierExpression(Name: "me", Location: _synthLoc))],
                Location: _synthLoc);
            var rawDecl = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: "raw",
                    Type: null,
                    Initializer: hijackedCall,
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc);
            var readCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "raw", Location: _synthLoc),
                    PropertyName: "unhijack",
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc);
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
            innerStatements = [rawDecl, callStmt];
        }

        return new DangerStatement(
            Body: new BlockStatement(Statements: innerStatements, Location: _synthLoc),
            Location: _synthLoc);
    }

    /// <summary>
    /// Gets the base type name without generic arguments.
    /// </summary>
    private static string GetBaseTypeName(string typeName)
    {
        int genericIndex = typeName.IndexOf(value: '[');
        return genericIndex >= 0
            ? typeName[..genericIndex]
            : typeName;
    }

    /// <summary>
    /// Checks if a type is a wrapper type (Viewed, Grasped, Shared, etc.).
    /// </summary>
    private bool IsWrapperType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return WrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewed, Inspected).
    /// </summary>
    private bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewed&lt;T&gt;).
    /// </summary>
    private TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // Wrapper types have their inner type as the first type argument
        if (wrapperType.TypeArguments is { Count: > 0 })
        {
            return wrapperType.TypeArguments[index: 0];
        }

        return null;
    }
}
