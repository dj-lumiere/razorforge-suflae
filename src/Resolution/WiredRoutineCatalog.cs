using System.Collections.Generic;
using System.Linq;

namespace Compiler.Resolution;

/// <summary>
/// The views (consumer lists) a wired-routine concept can participate in. Each historical
/// hard-coded list becomes a projection of <see cref="WiredRoutineCatalog"/> filtered by one flag.
/// </summary>
[System.Flags]
public enum WiredView
{
    None = 0,

    /// <summary>In <c>TypeRegistry.Capabilities._wiredRoutineMap</c>: capability gating
    /// (name → primary protocol + canonical wired routine).</summary>
    Capability = 1,

    /// <summary>In <c>SemanticVerifier.KnownWiredMethods</c>: the set of valid <c>$</c>-prefixed
    /// routine names a user may declare (drives the "unknown wired method" diagnostic).</summary>
    KnownWired = 2,

    /// <summary>In <c>SemanticVerifier.WiredToProtocols</c>: operator-declaration protocol
    /// requirement check (name → the protocols that permit declaring the operator).</summary>
    ProtocolDecl = 4,

    /// <summary>In <c>RoutineReachabilityPass.WiredRoutineNames</c>: names seeded live on every
    /// live concrete owner so operator-lowered bodies keep their link symbols.</summary>
    ReachabilitySeed = 8
}

/// <summary>Coarse classification of a wired routine, used by generation/lifecycle policy.</summary>
public enum WiredKind
{
    Creator, Comparison, Arithmetic, ArithmeticWrap, ArithmeticClamp, ArithmeticUnchecked,
    Bitwise, Shift, Unary, Unwrap, Container, Iteration, Indexing, Context, Lifecycle,
    Display, Hash, Copy, InPlaceArithmetic, InPlaceBitwise, InPlaceShift
}

/// <summary>One wired-routine concept (keyed by its bare canonical name, e.g. <c>$getitem</c>).</summary>
public sealed class WiredEntry
{
    public required string Name { get; init; }
    public required WiredKind Kind { get; init; }
    public required WiredView Views { get; init; }

    /// <summary>The protocols that materialise this routine. <c>[0]</c> is the primary/canonical
    /// protocol used by capability gating; the full list is the <see cref="WiredView.ProtocolDecl"/>
    /// requirement set. Empty when the routine is not protocol-bound (e.g. <c>$copy</c> is keyed on
    /// Assignable for capability but not declared via a protocol-operator).</summary>
    public IReadOnlyList<string> Protocols { get; init; } = [];

    /// <summary>The key under which this appears in the capability map. Defaults to <see cref="Name"/>;
    /// overridden for the failable index/iter forms (<c>$getitem!</c>/<c>$setitem!</c>/<c>$next!</c>).</summary>
    public string? CapabilityKeyOverride { get; init; }

    /// <summary>The canonical wired routine the capability gate looks up on the owner. Defaults to
    /// <see cref="CapabilityKey"/>; overridden for derived operators that share a base
    /// (e.g. <c>$ne</c>→<c>$eq</c>, <c>$lt</c>→<c>$cmp</c>, <c>$mod</c>→<c>$floordiv</c>).</summary>
    public string? CapabilityWiredOverride { get; init; }

    /// <summary>The exact token(s) seeded by reachability. Defaults to <c>[Name]</c>; overridden where
    /// reachability uses the failable form (<c>$next!</c>) or seeds extra siblings (<c>$unwrap!</c>).</summary>
    public IReadOnlyList<string>? ReachabilitySeedFormsOverride { get; init; }

    /// <summary>True for routines that must be emitted for every live owner regardless of call-site
    /// reachability — the unified-teardown lifecycle routines (<c>$destroy</c>/<c>$copy</c>).</summary>
    public bool AlwaysLive { get; init; }

    public string CapabilityKey => CapabilityKeyOverride ?? Name;
    public string CapabilityWired => CapabilityWiredOverride ?? CapabilityKey;
    public IReadOnlyList<string> ReachabilitySeedForms => ReachabilitySeedFormsOverride ?? [Name];
}

/// <summary>
/// Single source of truth for the compiler's built-in ("wired") routine names. Every historical
/// hard-coded list (capability map, known-wired set, operator→protocol map, reachability seed array)
/// is now a projection of <see cref="All"/> filtered by a <see cref="WiredView"/> flag. Adding or
/// renaming a wired routine is a one-line edit here; the projections (and their <c>#if DEBUG</c>
/// equality assertions at each old site) keep every consumer aligned.
/// </summary>
public static class WiredRoutineCatalog
{
    // Shorthand local aliases to keep the table readable.
    private const WiredView Cap = WiredView.Capability;
    private const WiredView Known = WiredView.KnownWired;
    private const WiredView Proto = WiredView.ProtocolDecl;
    private const WiredView Seed = WiredView.ReachabilitySeed;

    public static readonly IReadOnlyList<WiredEntry> All = BuildAll();

    private static WiredEntry[] BuildAll() =>
    [
        // ---- Creator / context / lifecycle (declarable, not protocol-bound) ----
        new() { Name = "$create",  Kind = WiredKind.Creator, Views = Known },
        new() { Name = "$enter",   Kind = WiredKind.Context, Views = Known },
        new() { Name = "$exit",    Kind = WiredKind.Context, Views = Known },
        new() { Name = "$destroy", Kind = WiredKind.Lifecycle, Views = Known, AlwaysLive = true },
        new() { Name = "$copy",    Kind = WiredKind.Copy, Views = Cap | Seed,
                Protocols = ["Assignable"], AlwaysLive = true },

        // ---- Display / hash ----
        new() { Name = "$represent", Kind = WiredKind.Display, Views = Cap | Known | Seed, Protocols = ["Representable"] },
        new() { Name = "$diagnose",  Kind = WiredKind.Display, Views = Cap | Known | Seed, Protocols = ["Diagnosable"] },
        new() { Name = "$hash",      Kind = WiredKind.Hash,    Views = Cap | Known | Seed, Protocols = ["Hashable"] },
        new() { Name = "$fast_hash", Kind = WiredKind.Hash,    Views = Cap,                Protocols = ["FastHashable"] },

        // ---- Comparison ($cmp family shares the $cmp body; $ne shares $eq) ----
        new() { Name = "$eq",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Equatable"] },
        new() { Name = "$ne",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Equatable"], CapabilityWiredOverride = "$eq" },
        new() { Name = "$cmp", Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"] },
        new() { Name = "$lt",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "$cmp" },
        new() { Name = "$le",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "$cmp" },
        new() { Name = "$gt",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "$cmp" },
        new() { Name = "$ge",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "$cmp" },

        // ---- Container / iteration / indexing ----
        new() { Name = "$contains",    Kind = WiredKind.Container, Views = Cap | Known | Proto | Seed, Protocols = ["Container"], CapabilityWiredOverride = "$contains" },
        new() { Name = "$notcontains", Kind = WiredKind.Container, Views = Cap | Known | Proto | Seed, Protocols = ["Container"], CapabilityWiredOverride = "$contains" },
        new() { Name = "$iter",        Kind = WiredKind.Iteration, Views = Cap | Known | Proto | Seed, Protocols = ["Iterable"] },
        new() { Name = "$next",        Kind = WiredKind.Iteration, Views = Cap | Known | Proto | Seed, Protocols = ["Iterator"],
                CapabilityKeyOverride = "$next!", ReachabilitySeedFormsOverride = ["$next!"] },
        new() { Name = "try_next",     Kind = WiredKind.Iteration, Views = Seed },
        new() { Name = "$getitem",     Kind = WiredKind.Indexing, Views = Cap | Known | Proto | Seed, Protocols = ["Indexable"], CapabilityKeyOverride = "$getitem!" },
        new() { Name = "$setitem",     Kind = WiredKind.Indexing, Views = Cap | Known | Proto | Seed, Protocols = ["Indexable"], CapabilityKeyOverride = "$setitem!" },

        // ---- Unwrap (Maybe / Result / Lookup) ----
        new() { Name = "$unwrap",    Kind = WiredKind.Unwrap, Views = Known | Seed, ReachabilitySeedFormsOverride = ["$unwrap", "$unwrap!"] },
        new() { Name = "$unwrap_or", Kind = WiredKind.Unwrap, Views = Known | Seed },

        // ---- Arithmetic (standard) ----
        new() { Name = "$add",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Addable", "DurationAddable"] },
        new() { Name = "$sub",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Subtractable", "DurationSubtractable"] },
        new() { Name = "$mul",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Multiplicable", "TextRepeatable", "Scalable"] },
        new() { Name = "$truediv",  Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Divisible", "ScalarDivisible"] },
        new() { Name = "$floordiv", Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["FloorDivisible", "ScalarFloorDivisible"] },
        new() { Name = "$mod",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["FloorDivisible"], CapabilityWiredOverride = "$floordiv" },
        new() { Name = "$pow",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Exponentiable"] },
        new() { Name = "$neg",      Kind = WiredKind.Unary,      Views = Cap | Known | Proto | Seed, Protocols = ["Negatable"] },

        // ---- Arithmetic (wrapping) ----
        new() { Name = "$add_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingAddable"] },
        new() { Name = "$sub_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingSubtractable"] },
        new() { Name = "$mul_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingMultiplicable"] },
        new() { Name = "$pow_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingExponentiable"] },

        // ---- Arithmetic (clamping) ----
        new() { Name = "$add_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingAddable"] },
        new() { Name = "$sub_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingSubtractable"] },
        new() { Name = "$mul_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingMultiplicable"] },
        new() { Name = "$truediv_clamp", Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingDivisible"] },
        new() { Name = "$pow_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingExponentiable"] },

        // ---- Arithmetic (unchecked) ----
        new() { Name = "$add_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedAddable"] },
        new() { Name = "$sub_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedSubtractable"] },
        new() { Name = "$mul_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedMultiplicable"] },
        new() { Name = "$truediv_unchecked",  Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedTrueDivisible"] },
        new() { Name = "$floordiv_unchecked", Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedFloorDivisible"] },
        new() { Name = "$mod_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedFloorDivisible"], CapabilityWiredOverride = "$floordiv_unchecked" },
        new() { Name = "$pow_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedExponentiable"] },

        // ---- Bitwise (the $bitand body covers and/or/xor) ----
        new() { Name = "$bitand", Kind = WiredKind.Bitwise, Views = Cap | Known | Proto | Seed, Protocols = ["Bitwiseable"], CapabilityWiredOverride = "$bitand" },
        new() { Name = "$bitor",  Kind = WiredKind.Bitwise, Views = Cap | Known | Proto | Seed, Protocols = ["Bitwiseable"], CapabilityWiredOverride = "$bitand" },
        new() { Name = "$bitxor", Kind = WiredKind.Bitwise, Views = Cap | Known | Proto | Seed, Protocols = ["Bitwiseable"], CapabilityWiredOverride = "$bitand" },
        new() { Name = "$bitnot", Kind = WiredKind.Unary,   Views = Cap | Known | Proto | Seed, Protocols = ["Invertible"] },

        // ---- Shift (the $ashl body covers all four) ----
        new() { Name = "$ashl", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "$ashl" },
        new() { Name = "$ashr", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "$ashl" },
        new() { Name = "$lshl", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "$ashl" },
        new() { Name = "$lshr", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "$ashl" },

        // ---- In-place arithmetic ($imod shares $ifloordiv) ----
        new() { Name = "$iadd",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceAddable"] },
        new() { Name = "$isub",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceSubtractable"] },
        new() { Name = "$imul",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceMultiplicable"] },
        new() { Name = "$itruediv",  Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceDivisible"] },
        new() { Name = "$ifloordiv", Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceFloorDivisible"] },
        new() { Name = "$imod",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceFloorDivisible"], CapabilityWiredOverride = "$ifloordiv" },
        new() { Name = "$ipow",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceExponentiable"] },

        // ---- In-place bitwise ($ibitor/$ibitxor share $ibitand) ----
        new() { Name = "$ibitand", Kind = WiredKind.InPlaceBitwise, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceBitwiseable"], CapabilityWiredOverride = "$ibitand" },
        new() { Name = "$ibitor",  Kind = WiredKind.InPlaceBitwise, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceBitwiseable"], CapabilityWiredOverride = "$ibitand" },
        new() { Name = "$ibitxor", Kind = WiredKind.InPlaceBitwise, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceBitwiseable"], CapabilityWiredOverride = "$ibitand" },

        // ---- In-place shift ($iashr/$ilshl/$ilshr share $iashl) ----
        new() { Name = "$iashl", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "$iashl" },
        new() { Name = "$iashr", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "$iashl" },
        new() { Name = "$ilshl", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "$iashl" },
        new() { Name = "$ilshr", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "$iashl" },
    ];

    // ---------------------------------------------------------------------------
    // Projections — each reproduces a historical hard-coded list exactly.
    // ---------------------------------------------------------------------------

    /// <summary>Capability map: <c>CapabilityKey → (primary Protocol, canonical wired routine)</c>.
    /// Reproduces <c>TypeRegistry.Capabilities._wiredRoutineMap</c>.</summary>
    public static Dictionary<string, (string Protocol, string WiredName)> BuildCapabilityMap()
    {
        var map = new Dictionary<string, (string, string)>(comparer: System.StringComparer.Ordinal);
        foreach (WiredEntry e in All.Where(predicate: e => e.Views.HasFlag(flag: Cap)))
            map[key: e.CapabilityKey] = (e.Protocols[index: 0], e.CapabilityWired);
        return map;
    }

    /// <summary>Valid declarable <c>$</c>-names. Reproduces <c>SemanticVerifier.KnownWiredMethods</c>.</summary>
    public static HashSet<string> BuildKnownWiredMethods() =>
        new(collection: All.Where(predicate: e => e.Views.HasFlag(flag: Known)).Select(selector: e => e.Name),
            comparer: System.StringComparer.Ordinal);

    /// <summary>Operator → permitting protocols. Reproduces <c>SemanticVerifier.WiredToProtocols</c>.</summary>
    public static Dictionary<string, List<string>> BuildWiredToProtocols() =>
        All.Where(predicate: e => e.Views.HasFlag(flag: Proto))
           .ToDictionary(keySelector: e => e.Name, elementSelector: e => e.Protocols.ToList());

    /// <summary>Names seeded live per concrete owner. Reproduces
    /// <c>RoutineReachabilityPass.WiredRoutineNames</c> (order-independent).</summary>
    public static string[] BuildReachabilitySeedNames() =>
        All.Where(predicate: e => e.Views.HasFlag(flag: Seed))
           .SelectMany(selector: e => e.ReachabilitySeedForms)
           .ToArray();

    // ---------------------------------------------------------------------------
    // Query API for the generation/lifecycle stages (S2/S3).
    // ---------------------------------------------------------------------------

    private static readonly Dictionary<string, WiredEntry> _byName =
        All.ToDictionary(keySelector: e => e.Name, comparer: System.StringComparer.Ordinal);

    /// <summary>Names that must be emitted for every live owner (the unified-teardown lifecycle
    /// routines). Used by the GMP gate-bypass and codegen always-live policy in S3.</summary>
    public static readonly IReadOnlySet<string> AlwaysLiveNames =
        All.Where(predicate: e => e.AlwaysLive).Select(selector: e => e.Name)
           .ToHashSet(comparer: System.StringComparer.Ordinal);

    public static bool TryGet(string name, out WiredEntry entry) => _byName.TryGetValue(key: name, value: out entry!);

    public static bool IsLifecycle(string name) =>
        _byName.TryGetValue(key: name, value: out WiredEntry? e) &&
        e.Kind is WiredKind.Lifecycle or WiredKind.Copy;

#if DEBUG
    // One-time consistency self-check (Debug builds only): every projection must reproduce the
    // historical hard-coded list EXACTLY. Runs on first access to the catalog (its static init).
    // The legacy literals below are the pre-S0 sources of truth, kept here as the reference oracle;
    // they are deleted in a follow-up once the catalog has proven stable. A mismatch throws with a
    // precise diff so a transcription slip is caught locally before any batch sweep.
    // NOTE: _selfChecked is declared at the END of this region — static field initializers run in
    // textual order, so it must come AFTER the _legacy* fields it reads (else they are still null).

    private static bool SelfCheck()
    {
        AssertSetEquals(label: "Capability", expected: _legacyCapabilityMap.Keys,
            actual: BuildCapabilityMap().Keys);
        foreach (var (k, v) in _legacyCapabilityMap)
        {
            var got = BuildCapabilityMap()[key: k];
            if (got.Protocol != v.Protocol || got.WiredName != v.WiredName)
            {
                string m = $"capability '{k}' = ({got.Protocol},{got.WiredName}) but legacy ({v.Protocol},{v.WiredName})";
                System.Console.Error.WriteLine(value: "CATALOG-SELFCHECK: " + m);
                throw new System.InvalidOperationException(message: m);
            }
        }
        AssertSetEquals(label: "KnownWired", expected: _legacyKnownWired, actual: BuildKnownWiredMethods());
        AssertSetEquals(label: "WiredToProtocols-keys", expected: _legacyWiredToProtocols.Keys,
            actual: BuildWiredToProtocols().Keys);
        foreach (var (k, v) in _legacyWiredToProtocols)
        {
            List<string> got = BuildWiredToProtocols()[key: k];
            if (!got.SequenceEqual(second: v))
            {
                string m = $"WiredToProtocols['{k}'] = [{string.Join(",", got)}] but legacy [{string.Join(",", v)}]";
                System.Console.Error.WriteLine(value: "CATALOG-SELFCHECK: " + m);
                throw new System.InvalidOperationException(message: m);
            }
        }
        AssertSetEquals(label: "ReachabilitySeed", expected: _legacyReachabilitySeed,
            actual: BuildReachabilitySeedNames());
        return true;
    }

    private static void AssertSetEquals(string label, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = new HashSet<string>(collection: expected, comparer: System.StringComparer.Ordinal);
        var a = new HashSet<string>(collection: actual, comparer: System.StringComparer.Ordinal);
        if (e.SetEquals(other: a)) return;
        var missing = e.Except(second: a).OrderBy(keySelector: x => x);
        var extra = a.Except(second: e).OrderBy(keySelector: x => x);
        string msg =
            $"WiredRoutineCatalog '{label}' diverged. Missing from catalog: [{string.Join(",", missing)}]. " +
            $"Extra in catalog: [{string.Join(",", extra)}].";
        System.Console.Error.WriteLine(value: "CATALOG-SELFCHECK: " + msg);
        throw new System.InvalidOperationException(message: msg);
    }

    private static readonly Dictionary<string, (string Protocol, string WiredName)> _legacyCapabilityMap =
        new(comparer: System.StringComparer.Ordinal)
        {
            ["$eq"] = ("Equatable", "$eq"), ["$ne"] = ("Equatable", "$eq"), ["$hash"] = ("Hashable", "$hash"),
            ["$fast_hash"] = ("FastHashable", "$fast_hash"), ["$cmp"] = ("Comparable", "$cmp"),
            ["$lt"] = ("Comparable", "$cmp"), ["$le"] = ("Comparable", "$cmp"), ["$gt"] = ("Comparable", "$cmp"),
            ["$ge"] = ("Comparable", "$cmp"), ["$contains"] = ("Container", "$contains"),
            ["$notcontains"] = ("Container", "$contains"), ["$getitem!"] = ("Indexable", "$getitem!"),
            ["$setitem!"] = ("Indexable", "$setitem!"), ["$iter"] = ("Iterable", "$iter"),
            ["$next!"] = ("Iterator", "$next!"), ["$represent"] = ("Representable", "$represent"),
            ["$diagnose"] = ("Diagnosable", "$diagnose"), ["$add"] = ("Addable", "$add"),
            ["$sub"] = ("Subtractable", "$sub"), ["$mul"] = ("Multiplicable", "$mul"),
            ["$truediv"] = ("Divisible", "$truediv"), ["$floordiv"] = ("FloorDivisible", "$floordiv"),
            ["$mod"] = ("FloorDivisible", "$floordiv"), ["$pow"] = ("Exponentiable", "$pow"),
            ["$neg"] = ("Negatable", "$neg"), ["$bitand"] = ("Bitwiseable", "$bitand"),
            ["$bitor"] = ("Bitwiseable", "$bitand"), ["$bitxor"] = ("Bitwiseable", "$bitand"),
            ["$bitnot"] = ("Invertible", "$bitnot"), ["$ashl"] = ("Shiftable", "$ashl"),
            ["$ashr"] = ("Shiftable", "$ashl"), ["$lshl"] = ("Shiftable", "$ashl"),
            ["$lshr"] = ("Shiftable", "$ashl"), ["$iadd"] = ("InPlaceAddable", "$iadd"),
            ["$isub"] = ("InPlaceSubtractable", "$isub"), ["$imul"] = ("InPlaceMultiplicable", "$imul"),
            ["$itruediv"] = ("InPlaceDivisible", "$itruediv"), ["$ifloordiv"] = ("InPlaceFloorDivisible", "$ifloordiv"),
            ["$imod"] = ("InPlaceFloorDivisible", "$ifloordiv"), ["$ipow"] = ("InPlaceExponentiable", "$ipow"),
            ["$ibitand"] = ("InPlaceBitwiseable", "$ibitand"), ["$ibitor"] = ("InPlaceBitwiseable", "$ibitand"),
            ["$ibitxor"] = ("InPlaceBitwiseable", "$ibitand"), ["$iashl"] = ("InPlaceShiftable", "$iashl"),
            ["$iashr"] = ("InPlaceShiftable", "$iashl"), ["$ilshl"] = ("InPlaceShiftable", "$iashl"),
            ["$ilshr"] = ("InPlaceShiftable", "$iashl"), ["$add_clamp"] = ("ClampingAddable", "$add_clamp"),
            ["$sub_clamp"] = ("ClampingSubtractable", "$sub_clamp"), ["$mul_clamp"] = ("ClampingMultiplicable", "$mul_clamp"),
            ["$truediv_clamp"] = ("ClampingDivisible", "$truediv_clamp"), ["$pow_clamp"] = ("ClampingExponentiable", "$pow_clamp"),
            ["$add_wrap"] = ("WrappingAddable", "$add_wrap"), ["$sub_wrap"] = ("WrappingSubtractable", "$sub_wrap"),
            ["$mul_wrap"] = ("WrappingMultiplicable", "$mul_wrap"), ["$pow_wrap"] = ("WrappingExponentiable", "$pow_wrap"),
            ["$add_unchecked"] = ("UncheckedAddable", "$add_unchecked"), ["$sub_unchecked"] = ("UncheckedSubtractable", "$sub_unchecked"),
            ["$mul_unchecked"] = ("UncheckedMultiplicable", "$mul_unchecked"), ["$truediv_unchecked"] = ("UncheckedTrueDivisible", "$truediv_unchecked"),
            ["$floordiv_unchecked"] = ("UncheckedFloorDivisible", "$floordiv_unchecked"), ["$mod_unchecked"] = ("UncheckedFloorDivisible", "$floordiv_unchecked"),
            ["$pow_unchecked"] = ("UncheckedExponentiable", "$pow_unchecked"), ["$copy"] = ("Assignable", "$copy"),
        };

    private static readonly string[] _legacyKnownWired =
    [
        "$create", "$add", "$sub", "$mul", "$truediv", "$floordiv", "$mod", "$pow",
        "$add_wrap", "$sub_wrap", "$mul_wrap", "$pow_wrap",
        "$add_clamp", "$sub_clamp", "$mul_clamp", "$truediv_clamp", "$pow_clamp",
        "$eq", "$ne", "$lt", "$le", "$gt", "$ge", "$cmp",
        "$bitand", "$bitor", "$bitxor", "$ashl", "$ashr", "$lshl", "$lshr",
        "$neg", "$bitnot", "$unwrap", "$unwrap_or", "$contains", "$notcontains",
        "$iter", "$next", "$getitem", "$setitem", "$enter", "$exit", "$destroy",
        "$represent", "$diagnose", "$hash",
        "$iadd", "$isub", "$imul", "$itruediv", "$ifloordiv", "$imod", "$ipow",
        "$ibitand", "$ibitor", "$ibitxor", "$iashl", "$iashr", "$ilshl", "$ilshr",
    ];

    private static readonly Dictionary<string, List<string>> _legacyWiredToProtocols = new()
    {
        ["$add"] = ["Addable", "DurationAddable"], ["$sub"] = ["Subtractable", "DurationSubtractable"],
        ["$mul"] = ["Multiplicable", "TextRepeatable", "Scalable"], ["$truediv"] = ["Divisible", "ScalarDivisible"],
        ["$floordiv"] = ["FloorDivisible", "ScalarFloorDivisible"], ["$mod"] = ["FloorDivisible"],
        ["$pow"] = ["Exponentiable"], ["$add_wrap"] = ["WrappingAddable"], ["$sub_wrap"] = ["WrappingSubtractable"],
        ["$mul_wrap"] = ["WrappingMultiplicable"], ["$pow_wrap"] = ["WrappingExponentiable"],
        ["$add_clamp"] = ["ClampingAddable"], ["$sub_clamp"] = ["ClampingSubtractable"],
        ["$mul_clamp"] = ["ClampingMultiplicable"], ["$truediv_clamp"] = ["ClampingDivisible"],
        ["$pow_clamp"] = ["ClampingExponentiable"], ["$eq"] = ["Equatable"], ["$ne"] = ["Equatable"],
        ["$cmp"] = ["Comparable"], ["$lt"] = ["Comparable"], ["$le"] = ["Comparable"], ["$gt"] = ["Comparable"],
        ["$ge"] = ["Comparable"], ["$bitand"] = ["Bitwiseable"], ["$bitor"] = ["Bitwiseable"],
        ["$bitxor"] = ["Bitwiseable"], ["$ashl"] = ["Shiftable"], ["$ashr"] = ["Shiftable"],
        ["$lshl"] = ["Shiftable"], ["$lshr"] = ["Shiftable"], ["$neg"] = ["Negatable"], ["$bitnot"] = ["Invertible"],
        ["$contains"] = ["Container"], ["$notcontains"] = ["Container"], ["$getitem"] = ["Indexable"],
        ["$setitem"] = ["Indexable"], ["$iter"] = ["Iterable"], ["$next"] = ["Iterator"],
        ["$iadd"] = ["InPlaceAddable"], ["$isub"] = ["InPlaceSubtractable"], ["$imul"] = ["InPlaceMultiplicable"],
        ["$itruediv"] = ["InPlaceDivisible"], ["$ifloordiv"] = ["InPlaceFloorDivisible"], ["$imod"] = ["InPlaceFloorDivisible"],
        ["$ipow"] = ["InPlaceExponentiable"], ["$ibitand"] = ["InPlaceBitwiseable"], ["$ibitor"] = ["InPlaceBitwiseable"],
        ["$ibitxor"] = ["InPlaceBitwiseable"], ["$iashl"] = ["InPlaceShiftable"], ["$iashr"] = ["InPlaceShiftable"],
        ["$ilshl"] = ["InPlaceShiftable"], ["$ilshr"] = ["InPlaceShiftable"],
    };

    private static readonly string[] _legacyReachabilitySeed =
    [
        "$represent", "$diagnose", "$hash", "$copy", "$eq", "$ne", "$cmp", "$lt", "$le", "$gt", "$ge",
        "$contains", "$notcontains", "$iter", "$next!", "try_next",
        "$add", "$sub", "$mul", "$truediv", "$floordiv", "$mod", "$pow", "$neg",
        "$add_wrap", "$sub_wrap", "$mul_wrap", "$pow_wrap",
        "$add_unchecked", "$sub_unchecked", "$mul_unchecked", "$truediv_unchecked", "$floordiv_unchecked",
        "$mod_unchecked", "$pow_unchecked",
        "$add_clamp", "$sub_clamp", "$mul_clamp", "$truediv_clamp", "$pow_clamp",
        "$bitand", "$bitor", "$bitxor", "$bitnot", "$ashl", "$ashr", "$lshl", "$lshr",
        "$iadd", "$isub", "$imul", "$itruediv", "$ifloordiv", "$imod", "$ipow",
        "$ibitand", "$ibitor", "$ibitxor", "$iashl", "$iashr", "$ilshl", "$ilshr",
        "$unwrap", "$unwrap!", "$unwrap_or", "$getitem", "$setitem",
    ];

    // Declared LAST so its initializer runs after every _legacy* field above (textual order).
    private static readonly bool _selfChecked = SelfCheck();
#endif
}
