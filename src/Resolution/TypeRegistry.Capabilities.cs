using System;
using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Resolution;

/// <summary>
/// Per-type capability queries. Answers "does this type support wired routine X?"
/// recursively, considering generic-def constraints on the routine in question.
/// </summary>
/// <remarks>
/// Used to gate codegen `declare` emission and reachability seeding for wired
/// routine families whose body has a built-in constraint surface that the registered
/// implementation can't satisfy for every instantiation — e.g. `Array[T, N].$eq`
/// declares `needs T obeys Equatable`, so `Array[Owned[X], 64]` should NOT carry an
/// `$eq` symbol because `Owned[X]` is not equatable.
///
/// Results are cached per FullName + protocol for the lifetime of the registry.
/// Cache entries are conservative — when the cache cannot decide (incomplete type
/// substitution, missing generic def), the query returns true so the caller does
/// not silently drop a needed symbol.
/// </remarks>
public sealed partial class TypeRegistry
{
    private const string ComparableProtocolName = "Comparable";
    private const string ContainsMethodName = "$contains";
    private const string BitAndMethodName = "$bitand";
    private const string ArithmeticShiftLeftMethodName = "$ashl";
    private const string ShiftableProtocolName = "Shiftable";
    private const string InPlaceBitAndMethodName = "$ibitand";
    private const string InPlaceShiftableProtocolName = "InPlaceShiftable";
    private const string InPlaceArithmeticShiftLeftMethodName = "$iashl";

    private readonly Dictionary<(string FullName, string Protocol), bool> _capabilityCache =
        new();

    /// <summary>
    /// Single source of truth: wired routine name -> (protocol it requires the owner to obey,
    /// canonical wired-routine name to look up on the owner). Used by both the constraint
    /// walker (which receives a `T obeys P` constraint and must check T) and the routine-
    /// applicability gate (which receives a routine and must check its owner).
    ///
    /// Keep this list aligned with `Standard/RazorForge/Core/Protocols/*.rf` — adding a new
    /// protocol with a wired routine means adding an entry here.
    /// </summary>
    private static readonly Dictionary<string, (string Protocol, string WiredName)>
        _wiredRoutineMap = new(comparer: StringComparer.Ordinal)
        {
            ["$eq"] = ("Equatable", "$eq"),
            ["$ne"] = ("Equatable", "$eq"),
            ["$hash"] = ("Hashable", "$hash"),
            ["$secure_hash"] = ("SecureHashable", "$secure_hash"),
            ["$cmp"] = (ComparableProtocolName, "$cmp"),
            ["$lt"] = (ComparableProtocolName, "$cmp"),
            ["$le"] = (ComparableProtocolName, "$cmp"),
            ["$gt"] = (ComparableProtocolName, "$cmp"),
            ["$ge"] = (ComparableProtocolName, "$cmp"),
            ["$contains"] = ("Container", ContainsMethodName),
            ["$notcontains"] = ("Container", ContainsMethodName),
            ["$getitem!"] = ("Indexable", "$getitem!"),
            ["$setitem!"] = ("Indexable", "$setitem!"),
            ["$iter"] = ("Iterable", "$iter"),
            ["$next!"] = ("Iterator", "$next!"),
            ["$represent"] = ("Representable", "$represent"),
            ["$diagnose"] = ("Diagnosable", "$diagnose"),
            ["$add"] = ("Addable", "$add"),
            ["$sub"] = ("Subtractable", "$sub"),
            ["$mul"] = ("Multiplicable", "$mul"),
            ["$truediv"] = ("Divisible", "$truediv"),
            ["$floordiv"] = ("FloorDivisible", "$floordiv"),
            ["$mod"] = ("FloorDivisible", "$floordiv"),
            ["$pow"] = ("Exponentiable", "$pow"),
            ["$neg"] = ("Negatable", "$neg"),
            ["$bitand"] = ("Bitwiseable", BitAndMethodName),
            ["$bitor"] = ("Bitwiseable", BitAndMethodName),
            ["$bitxor"] = ("Bitwiseable", BitAndMethodName),
            ["$bitnot"] = ("Invertible", "$bitnot"),
            ["$ashl"] = (ShiftableProtocolName, ArithmeticShiftLeftMethodName),
            ["$ashr"] = (ShiftableProtocolName, ArithmeticShiftLeftMethodName),
            ["$lshl"] = (ShiftableProtocolName, ArithmeticShiftLeftMethodName),
            ["$lshr"] = (ShiftableProtocolName, ArithmeticShiftLeftMethodName),
            ["$iadd"] = ("InPlaceAddable", "$iadd"),
            ["$isub"] = ("InPlaceSubtractable", "$isub"),
            ["$imul"] = ("InPlaceMultiplicable", "$imul"),
            ["$itruediv"] = ("InPlaceDivisible", "$itruediv"),
            ["$ifloordiv"] = ("InPlaceFloorDivisible", "$ifloordiv"),
            ["$imod"] = ("InPlaceFloorDivisible", "$ifloordiv"),
            ["$ipow"] = ("InPlaceExponentiable", "$ipow"),
            ["$ibitand"] = ("InPlaceBitwiseable", InPlaceBitAndMethodName),
            ["$ibitor"] = ("InPlaceBitwiseable", InPlaceBitAndMethodName),
            ["$ibitxor"] = ("InPlaceBitwiseable", InPlaceBitAndMethodName),
            ["$iashl"] = (InPlaceShiftableProtocolName, InPlaceArithmeticShiftLeftMethodName),
            ["$iashr"] = (InPlaceShiftableProtocolName, InPlaceArithmeticShiftLeftMethodName),
            ["$ilshl"] = (InPlaceShiftableProtocolName, InPlaceArithmeticShiftLeftMethodName),
            ["$ilshr"] = (InPlaceShiftableProtocolName, InPlaceArithmeticShiftLeftMethodName),
            ["$add_clamp"] = ("ClampingAddable", "$add_clamp"),
            ["$sub_clamp"] = ("ClampingSubtractable", "$sub_clamp"),
            ["$mul_clamp"] = ("ClampingMultiplicable", "$mul_clamp"),
            ["$truediv_clamp"] = ("ClampingDivisible", "$truediv_clamp"),
            ["$pow_clamp"] = ("ClampingExponentiable", "$pow_clamp"),
            ["$add_wrap"] = ("WrappingAddable", "$add_wrap"),
            ["$sub_wrap"] = ("WrappingSubtractable", "$sub_wrap"),
            ["$mul_wrap"] = ("WrappingMultiplicable", "$mul_wrap"),
            ["$pow_wrap"] = ("WrappingExponentiable", "$pow_wrap"),
            ["$add_unchecked"] = ("UncheckedAddable", "$add_unchecked"),
            ["$sub_unchecked"] = ("UncheckedSubtractable", "$sub_unchecked"),
            ["$mul_unchecked"] = ("UncheckedMultiplicable", "$mul_unchecked"),
            ["$truediv_unchecked"] = ("UncheckedTrueDivisible", "$truediv_unchecked"),
            ["$floordiv_unchecked"] = ("UncheckedFloorDivisible", "$floordiv_unchecked"),
            ["$mod_unchecked"] = ("UncheckedFloorDivisible", "$floordiv_unchecked"),
            ["$pow_unchecked"] = ("UncheckedExponentiable", "$pow_unchecked"),
        };

    /// <summary>
    /// Reverse map: protocol -> canonical wired routine that materialises it. Built once
    /// from <see cref="_wiredRoutineMap"/>; the protocol's "canonical" routine is the one
    /// whose wired name matches the protocol's primary operator.
    /// </summary>
    private static readonly Dictionary<string, string> _protocolToWired =
        BuildProtocolToWired();

    private static Dictionary<string, string> BuildProtocolToWired()
    {
        var result = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (var (_, (proto, wired)) in _wiredRoutineMap)
            result[key: proto] = wired;
        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> can host wired routine <paramref name="wiredName"/>
    /// for this concrete instantiation — i.e. all `T obeys P` constraints on the generic-def
    /// version of that routine are satisfied by <paramref name="type"/>'s type arguments,
    /// AND the routine is either defined directly or derivable through the protocol marker.
    /// </summary>
    public bool TypeHasWiredRoutine(TypeInfo type, string wiredName)
    {
        if (!_wiredRoutineMap.TryGetValue(key: wiredName, value: out var entry))
            return true;
        return HasCapability(type: type, protocol: entry.Protocol, wiredName: entry.WiredName);
    }

    /// <summary>Returns true if the type implements <c>Equatable</c> ($eq).</summary>
    public bool TypeHasEquality(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: "$eq");
    /// <summary>Returns true if the type implements <c>Containable</c> ($contains).</summary>
    public bool TypeHasContainment(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: ContainsMethodName);
    /// <summary>Returns true if the type implements <c>Hashable</c> ($hash).</summary>
    public bool TypeHasHashing(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: "$hash");
    /// <summary>Returns true if the type implements <c>Comparable</c> ($cmp).</summary>
    public bool TypeHasComparison(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: "$cmp");

    private bool HasCapability(TypeInfo type, string protocol, string wiredName)
    {
        var cacheKey = (type.FullName, protocol);
        if (_capabilityCache.TryGetValue(key: cacheKey, value: out bool cached))
            return cached;
        // Seed the cache with `true` before recursing so a self-referential type
        // (record containing itself via a wrapper) terminates rather than looping.
        // The conservative seed cannot produce a false-positive cycle: if any
        // step below proves the type lacks the capability, we overwrite to false.
        _capabilityCache[key: cacheKey] = true;
        bool result = ComputeCapability(type: type,
            protocol: protocol,
            wiredName: wiredName);
        _capabilityCache[key: cacheKey] = result;
        return result;
    }

    private bool ComputeCapability(TypeInfo type, string protocol, string wiredName)
    {
        // Generic parameters, error / blank types pass through — they're either further
        // substituted downstream or already a no-op.
        if (type is GenericParameterTypeInfo or ErrorTypeInfo || type.IsBlank) return true;

        // No backend-type shortcut: scalar primitives still define `$eq`/`$hash` explicitly
        // (see Core/Numerics/*.rf) and will be picked up by the LookupMethod fallback below.
        // The shortcut used to fire on every `@llvm("…")`-backed record — including aggregate-
        // backed records like `Array[T, N]` (`@llvm("[{N} x {T}]")`) — which masked the
        // generic-constraint check we need for correctness.

        // For a generic resolution G[T1, T2, ...], the generic def's wired-method may carry
        // `Ti obeys P` constraints. Each must hold for the corresponding type arg, where
        // the capability being demanded is keyed by P (not by the outer `protocol` we're
        // currently evaluating) — e.g. Array.$contains demands `T obeys Equatable`, so we
        // recurse for Equatable on T, not for Container.
        TypeInfo? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (genericDef != null && type.TypeArguments is { Count: > 0 } typeArgs &&
            genericDef.GenericParameters is { Count: > 0 } gParams &&
            gParams.Count == typeArgs.Count)
        {
            RoutineInfo? defMethod = LookupMethod(type: genericDef, methodName: wiredName);
            if (defMethod is { GenericConstraints: { Count: > 0 } constraints })
            {
                foreach (GenericConstraintDeclaration c in constraints)
                {
                    if (c.ConstraintType != ConstraintKind.Obeys ||
                        c.ConstraintTypes is not { Count: > 0 } protos) continue;
                    int idx = -1;
                    for (int i = 0; i < gParams.Count; i++)
                        if (gParams[index: i] == c.ParameterName) { idx = i; break; }
                    if (idx < 0) continue;

                    TypeInfo argType = typeArgs[index: idx];
                    foreach (TypeExpression protoExpr in protos)
                    {
                        // Each `T obeys P` constraint demands that the corresponding type
                        // arg has P's underlying capability. Look up the canonical wired
                        // routine for P from the central map; unknown protocols (e.g.
                        // marker traits without a wired routine) are skipped.
                        if (!_protocolToWired.TryGetValue(key: protoExpr.Name,
                                value: out string? requiredWired))
                            continue;
                        if (!HasCapability(type: argType,
                                protocol: protoExpr.Name,
                                wiredName: requiredWired))
                            return false;
                    }
                }
            }
        }

        // Direct support: type has the method registered (explicit or synthesised).
        if (LookupMethod(type: type, methodName: wiredName) != null) return true;

        // Marker conformance: the type obeys the named protocol — we expect a body to
        // appear eventually (via auto-synthesis) or for it to be an abstract marker.
        if (TypeObeysProtocol(type: type, protocolName: protocol)) return true;

        return false;
    }

    private bool TypeObeysProtocol(TypeInfo type, string protocolName) // NOSONAR S3776
    {
        IReadOnlyList<TypeInfo>? implemented = type switch
        {
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };
        if (implemented == null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return implemented.Any(p => Walk(p, protocolName, seen));

        bool Walk(TypeInfo candidate, string target, HashSet<string> seenSet)
        {
            if (!seenSet.Add(item: candidate.Name)) return false;
            if (candidate.Name == target) return true;
            TypeInfo latest = LookupType(name: candidate.Name) ?? candidate;
            if (latest is ProtocolTypeInfo proto)
                return proto.ParentProtocols.Any(parent => Walk(parent, target, seenSet));
            return false;
        }
    }
}
