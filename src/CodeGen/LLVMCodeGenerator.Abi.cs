using System.Text;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// ABI boundary coercion for struct-record values crossing a call boundary.
///
/// LLVM does not lower first-class struct values to the target C ABI — passing/returning a record
/// as a plain <c>%"Record.Foo"</c> value has no portable meaning and miscompiles on some targets
/// (notably aarch64 by-value args, x86 over-aligned structs). The frontend must coerce structs to
/// their ABI representation at each call boundary. This file is the single place that ABI knowledge
/// lives; field access elsewhere keeps the natural struct layout untouched.
///
/// See internal-wiki/v0.1.x-struct-abi-boundary-coercion.md for the full 3-ABI matrix and phasing.
/// Phase 1 implements only <see cref="AbiKind.Direct"/> vs <see cref="AbiKind.Indirect"/>; register
/// coercion (CoerceToInt / CoercePair / HFA) for small/medium structs is deferred to later phases —
/// until then anything not Indirect stays Direct (LLVM's natural lowering, which already happens to
/// match the ABI for simple ≤16-byte integer aggregates).
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>How a struct-record value is passed/returned across a call boundary.</summary>
    internal enum AbiKind
    {
        /// <summary>Pass/return the value as-is (scalars, @llvm records, and — for now — small structs).</summary>
        Direct,

        /// <summary>Pass via a pointer-to-copy argument / return via a hidden <c>sret</c> pointer.</summary>
        Indirect
    }

    /// <summary>The ABI passing decision for one type. A lightweight discriminated union.</summary>
    internal readonly record struct AbiPassing(AbiKind Kind, string? DirectType = null)
    {
        public static AbiPassing Direct(string llvm) => new(Kind: AbiKind.Direct, DirectType: llvm);
        public static readonly AbiPassing Indirect = new(Kind: AbiKind.Indirect);
    }

    /// <summary>
    /// A value type that crosses a call boundary BY VALUE and is therefore in scope for ABI
    /// coercion: a struct record with no <c>@llvm</c> backend and no carrier kind (Result/Lookup/
    /// Maybe are codegen-owned and handled by their own paths). Tuples qualify — they are
    /// <c>RecordTypeInfo</c> with item0..itemN fields. Excludes <c>@llvm</c> scalar/aggregate
    /// records, entities, wrappers, protocols, and generic definitions.
    /// </summary>
    private static bool IsByValueStructRecord(TypeInfo type) =>
        type is RecordTypeInfo
        {
            HasDirectBackendType: false, IsGenericDefinition: false, CarrierKind: CarrierKind.None
        };

    /// <summary>
    /// Classifies how <paramref name="type"/> crosses a call boundary on the current target.
    /// Phase 1: struct records larger than the in-register limit go <see cref="AbiKind.Indirect"/>;
    /// everything else is <see cref="AbiKind.Direct"/>. The size threshold mirrors the per-ABI rule
    /// already used by <c>NeedsCExternSret</c>: Win-x64 MSVC passes aggregates &gt; 8 bytes
    /// indirectly, SysV / AAPCS64 pass aggregates &gt; 16 bytes indirectly.
    /// </summary>
    private AbiPassing AbiClassify(TypeInfo type)
    {
        if (!IsByValueStructRecord(type: type))
        {
            return AbiPassing.Direct(llvm: GetLlvmType(type: type));
        }

        int size = GetTypeSize(type: type);
        bool indirect = _target.TargetOS == "windows" ? size > 8 : size > 16;
        return indirect ? AbiPassing.Indirect : AbiPassing.Direct(llvm: GetLlvmType(type: type));
    }

    /// <summary>
    /// Whether <paramref name="routine"/> returns its value through a hidden <c>sret</c> pointer
    /// (the <see cref="AbiKind.Indirect"/> return form). Async variants return carriers
    /// (Result/Lookup/i1) through their own lowering and are never plain-sret.
    /// </summary>
    private bool ReturnsViaSret(RoutineInfo routine)
    {
        // Async routines (suspended/threaded) hand their result back through their own ABI — the
        // Task[T] result cell / continuation, NOT a plain sret pointer. Forcing sret here breaks the
        // thread-boundary aggregate handoff (a threaded routine returning a record segfaulted). Leave
        // them on the existing by-value path.
        if (routine.AsyncStatus is AsyncStatus.Suspended or AsyncStatus.Threaded)
        {
            return false;
        }

        if (routine.FailableVariant is FailableVariant.Lookup or FailableVariant.Check
            or FailableVariant.TryBool or FailableVariant.Try)
        {
            return false;
        }

        return routine.ReturnType != null
               && AbiClassify(type: routine.ReturnType).Kind == AbiKind.Indirect;
    }

    /// <summary>
    /// A type whose copy is trivial — a plain bitwise duplicate is sound, with no managed
    /// <c>$copy</c> to bump a refcount and no managed <c>$destroy</c> to balance. Decided by the
    /// SAME oracle the copy-lowering and teardown passes use: <see cref="TypeRegistry.GetLifecycle"/>
    /// returns a non-null <c>Copy</c> exactly when the type (or, recursively, a field of it) is a
    /// managed leaf like <c>Text</c>/<c>Decimal</c>; a null <c>Copy</c> means trivially copyable.
    /// Tuples and composite records are handled by the recursion inside GetLifecycle.
    ///
    /// This gates <c>byval</c>: byval is a bitwise memcpy that bypasses the managed protocol, so for
    /// a managed record the callee's <c>$destroy</c> of its byval copy would free state the caller
    /// still owns — a double-free. Because the copy-lowering pass injects a retaining <c>$copy</c>
    /// for EXACTLY the same (non-trivial) types, gating on this oracle also guarantees byval never
    /// races that injected copy: trivially-copyable args get no <c>$copy</c>, so byval is the only
    /// duplication and it is sound.
    /// </summary>
    private bool IsTriviallyCopyableRecord(TypeInfo type) =>
        _registry.GetLifecycle(type: type).Copy == null;

    /// <summary>
    /// Whether the explicit value parameter <paramref name="paramType"/> of <paramref name="routine"/>
    /// is passed BY VALUE through a hidden <c>ptr byval(%T)</c> copy (the
    /// <see cref="AbiKind.Indirect"/> argument form). Requires the type to be trivially copyable (see
    /// <see cref="IsTriviallyCopyableRecord"/>) — byval is a bitwise copy and is unsound for managed
    /// records (which keep the existing by-value path that the copy-lowering pass balances with an
    /// explicit <c>$copy</c>). EXCLUDES async routines: suspended/threaded workers receive their args
    /// through their own handoff (the thread cell / closure), not the C calling convention, so byval
    /// at that boundary mismatches the worker's value-typed parameter. Callers consult this only AFTER
    /// excluding by-ref receivers (<c>me</c>) and thread-shareable args.
    /// </summary>
    private bool ParameterPassedByval(RoutineInfo routine, TypeInfo paramType) =>
        !routine.IsAsync
        && AbiClassify(type: paramType).Kind == AbiKind.Indirect
        && IsTriviallyCopyableRecord(type: paramType);

    /// <summary>
    /// If <paramref name="parameterType"/> is an Indirect (byval) struct parameter and
    /// <paramref name="argValue"/> is the matching struct value, spill it to a stack slot and
    /// rewrite the argument to <c>ptr byval(%T)</c>. Returns true (with <paramref name="newValue"/>
    /// /<paramref name="newType"/> set) when it applied; false to leave the argument unchanged.
    /// The alloca goes in the entry block; the store is emitted at the call site.
    /// </summary>
    private bool TryCoerceArgToByval(StringBuilder sb, string argValue, TypeInfo actualType,
        TypeInfo parameterType, RoutineInfo callee, out string newValue, out string newType)
    {
        newValue = argValue;
        newType = GetParameterLlvmType(type: parameterType);
        if (!ParameterPassedByval(routine: callee, paramType: parameterType))
        {
            return false;
        }

        // Only the direct struct-match case: actual value already has the parameter's struct type.
        // (Single-field-record unwrap and other mismatches keep their existing handling.)
        string t = GetParameterLlvmType(type: parameterType);
        if (GetLlvmType(type: actualType) != t)
        {
            return false;
        }

        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: t);
        EmitLine(sb: sb, line: $"  store {t} {argValue}, ptr {slot}");
        newValue = slot;
        newType = $"ptr byval({t})";
        return true;
    }
}
