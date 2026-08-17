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
/// The struct-ABI boundary-coercion design covers the full 3-ABI matrix and phasing.
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
        /// <summary>Pass/return the value as-is (scalars, @llvm records, float-bearing small structs).</summary>
        Direct,

        /// <summary>
        /// Pass/return reinterpreted as integer register chunks (one <c>iN</c>, or an inline
        /// <c>{i64, iM}</c> pair) — the Phase 2 register-coercion form for small all-integer structs.
        /// Eliminates sub-word fields at the boundary (the aarch64 spill bug) without going indirect.
        /// </summary>
        Coerce,

        /// <summary>Pass via a pointer-to-copy argument / return via a hidden <c>sret</c> pointer.</summary>
        Indirect
    }

    /// <summary>The ABI passing decision for one type. A lightweight discriminated union.</summary>
    internal readonly record struct AbiPassing(AbiKind Kind, string? DirectType = null,
        string? CoerceType = null)
    {
        public static AbiPassing Direct(string llvm) => new(Kind: AbiKind.Direct, DirectType: llvm);
        public static AbiPassing Coerce(string llvm) => new(Kind: AbiKind.Coerce, CoerceType: llvm);
        public static readonly AbiPassing Indirect = new(Kind: AbiKind.Indirect);
    }

    /// <summary>The integer width (<c>i8/i16/i32/i64</c>) covering a chunk of <paramref name="bytes"/>.</summary>
    private static string ChunkIntType(int bytes) => bytes switch
    {
        <= 1 => "i8",
        <= 2 => "i16",
        <= 4 => "i32",
        _ => "i64"
    };

    /// <summary>
    /// Whether <paramref name="type"/> (a struct record) contains a floating-point field, directly
    /// or nested. Such structs are NOT integer-coerced in Phase 2 — on SysV/AAPCS64 their fields are
    /// SSE/FP-classified (and may be homogeneous-float aggregates), which Phase 3 handles; integer
    /// coercion would place them in the wrong register file. Left <see cref="AbiKind.Direct"/> here.
    /// </summary>
    private bool StructHasFloatField(TypeInfo type)
    {
        if (type is not RecordTypeInfo { MemberVariables: { } members })
        {
            return false;
        }

        foreach (MemberVariableInfo m in members)
        {
            string llvm = GetLlvmType(type: m.Type);
            if (llvm is "half" or "float" or "double" or "fp128")
            {
                return true;
            }

            if (m.Type is RecordTypeInfo { HasDirectBackendType: false } && StructHasFloatField(type: m.Type))
            {
                return true;
            }
        }

        return false;
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
    /// <list type="bullet">
    /// <item>Not an in-scope by-value struct (scalars/@llvm/entities/wrappers) → <see cref="AbiKind.Direct"/>.</item>
    /// <item>Float-bearing struct → Direct for now (Phase 3 / HFA handles the FP register file).</item>
    /// <item><b>Win-x64 MSVC:</b> all-integer struct of size 1/2/4/8 → <see cref="AbiKind.Coerce"/> to
    ///   the exact <c>iN</c>; any other size (3/5/6/7 or &gt; 8) → <see cref="AbiKind.Indirect"/>.</item>
    /// <item><b>SysV / AAPCS64:</b> ≤ 8 bytes → Coerce to one integer chunk; 9–16 bytes → Coerce to an
    ///   inline <c>{i64, iM}</c> pair; &gt; 16 bytes → Indirect.</item>
    /// </list>
    /// Integer coercion reinterprets the struct's bytes as word chunks, eliminating sub-word fields
    /// at the boundary (the aarch64 spill class) while keeping the value in registers.
    /// </summary>
    private AbiPassing AbiClassify(TypeInfo type)
    {
        if (!IsByValueStructRecord(type: type))
        {
            return AbiPassing.Direct(llvm: GetLlvmType(type: type));
        }

        int size = GetTypeSize(type: type);
        // Empty record: nothing to pass — leave as-is.
        if (size == 0)
        {
            return AbiPassing.Direct(llvm: GetLlvmType(type: type));
        }

        // Float-bearing aggregates use the SSE/FP register file (and may be HFAs); integer coercion
        // would misplace them. Deferred to Phase 3 — keep LLVM's natural lowering for now.
        if (StructHasFloatField(type: type))
        {
            return AbiPassing.Direct(llvm: GetLlvmType(type: type));
        }

        if (_target.TargetOS == "windows")
        {
            // MS x64: only sizes 1/2/4/8 ride in a register (as that exact integer); all else indirect.
            return size is 1 or 2 or 4 or 8
                ? AbiPassing.Coerce(llvm: ChunkIntType(bytes: size))
                : AbiPassing.Indirect;
        }

        // SysV / AAPCS64: up to two eightbyte integer chunks in registers, else memory.
        if (size <= 8)
        {
            return AbiPassing.Coerce(llvm: ChunkIntType(bytes: size));
        }

        if (size <= 16)
        {
            return AbiPassing.Coerce(llvm: $"{{ i64, {ChunkIntType(bytes: size - 8)} }}");
        }

        return AbiPassing.Indirect;
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
    /// The ABI register type a routine's struct return is COERCED to (e.g. <c>i64</c> /
    /// <c>{ i64, i32 }</c>), or null when the return is not coerced (Direct, Indirect, or an async /
    /// failable-variant routine whose result uses its own lowering). The function header then returns
    /// this type, every <c>return</c> reinterprets the struct value into it, and the caller
    /// reinterprets the result back into the struct.
    /// </summary>
    private string? ReturnCoerceType(RoutineInfo routine)
    {
        if (routine.AsyncStatus is AsyncStatus.Suspended or AsyncStatus.Threaded)
        {
            return null;
        }

        if (routine.FailableVariant is FailableVariant.Lookup or FailableVariant.Check
            or FailableVariant.TryBool or FailableVariant.Try)
        {
            return null;
        }

        if (routine.ReturnType == null)
        {
            return null;
        }

        AbiPassing p = AbiClassify(type: routine.ReturnType);
        return p.Kind == AbiKind.Coerce ? p.CoerceType : null;
    }

    /// <summary>
    /// Reinterprets a struct VALUE into its ABI register type via a stack round-trip (store the
    /// struct, load the wider/equal integer form). The slot is the ABI type, which is always at
    /// least as large as the struct, so the store fits; any bytes past the struct are ABI don't-care.
    /// </summary>
    private string CoerceStructToAbi(StringBuilder sb, string structValue, string structLlvm,
        string abiType)
    {
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: abiType);
        EmitLine(sb: sb, line: $"  store {structLlvm} {structValue}, ptr {slot}");
        string v = NextTemp();
        EmitLine(sb: sb, line: $"  {v} = load {abiType}, ptr {slot}");
        return v;
    }

    /// <summary>Reverse of <see cref="CoerceStructToAbi"/>: an ABI register value → the struct value.</summary>
    private string CoerceAbiToStruct(StringBuilder sb, string abiValue, string abiType,
        string structLlvm)
    {
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: abiType);
        EmitLine(sb: sb, line: $"  store {abiType} {abiValue}, ptr {slot}");
        string v = NextTemp();
        EmitLine(sb: sb, line: $"  {v} = load {structLlvm}, ptr {slot}");
        return v;
    }

    /// <summary>
    /// A type whose store is trivial — a plain bitwise duplicate is sound, with no managed
    /// <c>store</c> to bump a refcount and no managed <c>destroy</c> to balance. Decided by the
    /// SAME oracle the copy-lowering and teardown passes use: <see cref="Compiler.Resolution.TypeRegistry.GetLifecycle(TypeModel.Types.TypeInfo)"/>
    /// returns a non-null <c>Store</c> exactly when the type (or, recursively, a field of it) is a
    /// managed leaf like <c>Text</c>/<c>Decimal</c>; a null <c>Store</c> means trivially storable.
    /// Tuples and composite records are handled by the recursion inside GetLifecycle.
    ///
    /// This gates <c>byval</c>: byval is a bitwise memcpy that bypasses the managed protocol, so for
    /// a managed record the callee's <c>destroy</c> of its byval copy would free state the caller
    /// still owns — a double-free. Because the copy-lowering pass injects a retaining <c>store</c>
    /// for EXACTLY the same (non-trivial) types, gating on this oracle also guarantees byval never
    /// races that injected store: trivially-storable args get no <c>store</c>, so byval is the only
    /// duplication and it is sound.
    /// </summary>
    private bool IsTriviallyStorableRecord(TypeInfo type) =>
        _registry.GetLifecycle(type: type).Store == null;

    /// <summary>
    /// Whether the explicit value parameter <paramref name="paramType"/> of <paramref name="routine"/>
    /// is passed BY VALUE through a hidden <c>ptr byval(%T)</c> copy (the
    /// <see cref="AbiKind.Indirect"/> argument form). Requires the type to be trivially storable (see
    /// <see cref="IsTriviallyStorableRecord"/>) — byval is a bitwise copy and is unsound for managed
    /// records (which keep the existing by-value path that the copy-lowering pass balances with an
    /// explicit <c>store</c>). EXCLUDES async routines: suspended/threaded workers receive their args
    /// through their own handoff (the thread cell / closure), not the C calling convention, so byval
    /// at that boundary mismatches the worker's value-typed parameter. Callers consult this only AFTER
    /// excluding by-ref receivers (<c>me</c>) and thread-shareable args.
    /// </summary>
    private bool ParameterPassedByval(RoutineInfo routine, TypeInfo paramType) =>
        !routine.IsAsync
        && AbiClassify(type: paramType).Kind == AbiKind.Indirect
        && IsTriviallyStorableRecord(type: paramType);

    /// <summary>
    /// The ABI register type a value parameter is COERCED to (e.g. <c>i64</c> / <c>{ i64, i32 }</c>),
    /// or null when the parameter is not register-coerced. Unlike byval, coercion needs NO trivial-
    /// copyability gate: it passes the struct's VALUE (reinterpreted as integers) and the callee
    /// reconstructs the same value, so ownership is identical to the existing by-value path (the
    /// copy-lowering pass already balances any managed <c>store</c>/<c>destroy</c>). Excludes async
    /// routines, whose workers receive args through their own cell/closure handoff.
    /// </summary>
    private string? ParameterCoerceType(RoutineInfo routine, TypeInfo paramType)
    {
        if (routine.IsAsync)
        {
            return null;
        }

        AbiPassing p = AbiClassify(type: paramType);
        return p.Kind == AbiKind.Coerce ? p.CoerceType : null;
    }

    /// <summary>
    /// If <paramref name="parameterType"/> is register-coerced for <paramref name="callee"/>,
    /// reinterprets the struct argument value into its ABI integer form and rewrites the argument.
    /// Returns true (with <paramref name="newValue"/>/<paramref name="newType"/> set) when applied.
    /// </summary>
    private bool TryCoerceArgToRegister(StringBuilder sb, string argValue, TypeInfo parameterType,
        RoutineInfo callee, out string newValue, out string newType)
    {
        newValue = argValue;
        newType = GetParameterLlvmType(type: parameterType);
        string? coerce = ParameterCoerceType(routine: callee, paramType: parameterType);
        if (coerce == null)
        {
            return false;
        }

        newValue = CoerceStructToAbi(sb: sb, structValue: argValue,
            structLlvm: GetLlvmType(type: parameterType), abiType: coerce);
        newType = coerce;
        return true;
    }

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
