using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Tokenizer;
using Compiler.Postprocessing;
using Compiler.Postprocessing.Passes;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Synthesis;

/// <summary>
/// Generates real AST bodies for <c>IsSynthesized = true</c> stub routines on record, entity,
/// and crashable types. Runs as a global pass after all per-file desugaring (Phase 4a).
///
/// <para>Generated bodies (keyed by <c>RoutineInfo.RegistryKey</c> ??<c>ctx.VariantBodies</c>):</para>
/// <list type="bullet">
///   <item><c>$eq</c>   -> field-by-field <c>==</c> AND-chain for concrete <see cref="RecordTypeInfo"/>, <see cref="EntityTypeInfo"/>, <see cref="TupleTypeInfo"/>.</item>
///   <item><c>$hash</c> -> XOR-chain of <c>me.f.$hash()</c> calls for records, entities, tuples.</item>
///   <item><c>$represent</c> / <c>$diagnose</c> -> f-string body for <see cref="RecordTypeInfo"/> and
///         <see cref="EntityTypeInfo"/>, including generic definitions (monomorphization substitutes type params).</item>
///   <item><c>$represent</c> on crashable ??<c>return me.crash_message()</c>.</item>
///   <item><c>$diagnose</c> on crashable -> f-string <c>Module.Name(crash_message, field: val, ...)</c>.</item>
///   <item><c>Text.$create(from: T)</c> ??<c>return from.$represent()</c>.</item>
/// </list>
///
/// <para>Not generated here:</para>
/// <list type="bullet">
///   <item><see cref="VariantTypeInfo"/> bodies — pattern dispatch on numeric value; not
///         expressible in plain AST. Emitted by <c>ErrorHandlingVariantPass</c>.</item>
///   <item>Records with <c>HasDirectBackendType</c> — intrinsic types with no RF member
///         variables (skipped early in <see cref="HandleRecord"/>).</item>
///   <item><c>Maybe[T].$represent</c> / <c>$diagnose</c> — defined explicitly in
///         <c>Core/Errors/Maybe.rf</c> (treated as user code, not synthesized).</item>
/// </list>
/// </summary>
public sealed class WiredRoutinePass(DesugaringContext ctx)
{
    private const string RepresentMethodName = "$represent";
    private const string DiagnoseMethodName = "$diagnose";
    private const string HashMethodName = "$hash";
    private const string BitXorMethodName = "$bitxor";
    private const string ResultVarName = "result";
    private const string FirstVarName = "first";
    private const string OtherParamName = "other";

    private static readonly SourceLocation _synthLoc =
        new(FileName: "", Line: 0, Column: 0, Position: 0);

    /// <summary>Synthesizes and registers all wired routines for the current program.</summary>
    public void RunGlobal() // NOSONAR S3776
    {
        TypeInfo? textType = ctx.Registry.LookupType(name: "Text");
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeInfo? s32Type = ctx.Registry.LookupType(name: "S32");
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        TypeInfo? byteSizeType = ctx.Registry.LookupType(name: "ByteSize");
        TypeInfo? logicBreachedErrorType = ctx.Registry.LookupType(name: "LogicBreachedError");
        TypeInfo? typeKindType = ctx.Registry.LookupType(name: "TypeKind");
        TypeInfo? listTypeDef = ctx.Registry.LookupType(name: "List");
        TypeInfo? listTextType = listTypeDef != null && textType != null
            ? ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef, typeArguments: [textType])
            : null;
        if (textType == null || boolType == null)
            return;

        foreach (RoutineInfo routine in ctx.Registry.GetAllRoutines())
        {
            if (!routine.IsSynthesized) continue;
            if (ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;
            if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;

            // Skip if an explicit (non-synthesized) implementation already exists in the registry.
            // This prevents synthesized bodies from overriding custom stdlib implementations
            // such as Marked[T,P].$represent / $diagnose defined in Marked.rf.
            if (routine.OwnerType != null &&
                ctx.Registry.GetMethodsForType(type: routine.OwnerType)
                    .Any(r => r.Name == routine.Name && !r.IsSynthesized))
                continue;

            // BuilderService constant routines apply to all owner types -> check by name first.
            if (routine.OwnerType != null
                && TryHandleBuilderServiceConstant(routine: routine, textType: textType,
                    u64Type: u64Type, s64Type: s64Type, boolType: boolType,
                    typeKindType: typeKindType, listTextType: listTextType,
                    byteSizeType: byteSizeType))
                continue;

            // Standalone BuilderService constants (no owner type): page_size, target_os, etc.
            if (routine.OwnerType == null
                && TryHandleStandaloneBuilderServiceConstant(routine: routine,
                    textType: textType, u64Type: u64Type,
                    byteSizeType: byteSizeType))
                continue;

            // Unified destructor: synthesize the auto-derived `$destroy()` body. Composite
            // record/entity/crashable types recurse into their owned fields; scalar kinds
            // (choices, flags, `@llvm`-backed primitives, tuples, variants) are no-ops. The
            // leaf RC/ptr behaviour (Hijacked → invalidate, Retained/Tracked → controller,
            // Viewed/Grasped → no-op) lives in hand-written wrapper `$destroy`s, so those are
            // never auto-derived (they already exist).
            if (routine is { Name: "$destroy", Parameters.Count: 0 })
            {
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildDestroyBody(owner: routine.OwnerType);
                continue;
            }

            switch (routine.OwnerType)
            {
                case TupleTypeInfo tuple:
                    HandleTuple(routine: routine, tuple: tuple, textType: textType,
                        s32Type: s32Type);
                    break;

                case ChoiceTypeInfo choice:
                    HandleChoice(routine: routine, choice: choice, textType: textType,
                        boolType: boolType, logicBreachedErrorType: logicBreachedErrorType,
                        u64Type: u64Type, s64Type: s64Type, listTypeDef: listTypeDef);
                    break;

                case FlagsTypeInfo flags:
                    HandleFlags(routine: routine, flags: flags, textType: textType,
                        boolType: boolType, u64Type: u64Type, listTypeDef: listTypeDef);
                    break;

                case RecordTypeInfo record:
                    HandleRecord(routine: routine, record: record,
                        textType: textType, boolType: boolType, s32Type: s32Type);
                    break;

                case EntityTypeInfo entity:
                    HandleEntity(routine: routine, entity: entity, textType: textType,
                        boolType: boolType);
                    break;

                case CrashableTypeInfo crashable:
                    HandleCrashable(routine: routine, crashable: crashable, textType: textType);
                    break;

                case VariantTypeInfo variant:
                    HandleVariant(routine: routine, variant: variant, textType: textType);
                    break;
            }
        }

        // Tuple types appear only as local variable / expression types and never as routine
        // signatures, so TypeLivenessPass cannot seed them — `GetAllRoutines()` filters them
        // out via `IsConcreteTypeLive`. Iterate them directly from `_resolutions` (via
        // `GetTypesWithMethods`) so their wired bodies are synthesized regardless of liveness.
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMethods())
        {
            if (type is not TupleTypeInfo tuple) continue;
            foreach (RoutineInfo routine in ctx.Registry.GetMethodsForType(type))
            {
                if (!routine.IsSynthesized) continue;
                if (ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                HandleTuple(routine: routine, tuple: tuple, textType: textType,
                    s32Type: s32Type);
            }
        }

        // GetAllRoutines() filters out generic-definition owner types to prevent T/K,V placeholders
        // in LLVM. However, BuilderService routines on generic defs return only fixed literals or
        // empty collections — they never reference the generic parameters. GMP needs these bodies
        // to emit the generic-def LLVM function (e.g. @Collections.BTreeDictNode.member_variable_count)
        // so that wrapper forwarders for Hijacked[BTreeDictNode] have a valid callee.
        RunForGenericDefBuilderServiceRoutines(textType: textType, u64Type: u64Type,
            s64Type: s64Type, boolType: boolType, typeKindType: typeKindType,
            listTextType: listTextType, byteSizeType: byteSizeType);

        // Synthesize wired routines ($eq, $hash, $represent, $diagnose) for
        // generic def entity/record types that have no source-defined implementation.
        // GMP needs a body in VariantBodies[genericDefKey] to rewrite into concrete instances.
        RunForGenericDefWiredRoutines(textType: textType, boolType: boolType,
            s32Type: s32Type);
    }

    private void RunForGenericDefBuilderServiceRoutines(
        TypeInfo textType, TypeInfo? u64Type, TypeInfo? s64Type, TypeInfo? boolType,
        TypeInfo? typeKindType, TypeInfo? listTextType, TypeInfo? byteSizeType)
    {
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMethods())
        {
            if (!type.IsGenericDefinition) continue;
            foreach (RoutineInfo routine in ctx.Registry.GetMethodsForType(type))
            {
                if (!routine.IsSynthesized) continue;
                if (!BuilderInfoProvider.IsBuilderServiceRoutine(name: routine.Name)) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                TryHandleBuilderServiceConstant(routine: routine, textType: textType,
                    u64Type: u64Type, s64Type: s64Type, boolType: boolType,
                    typeKindType: typeKindType, listTextType: listTextType,
                    byteSizeType: byteSizeType);
            }
        }
    }

    private void RunForGenericDefWiredRoutines(TypeInfo textType, TypeInfo boolType,
        TypeInfo? s32Type)
    {
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMethods())
        {
            if (!type.IsGenericDefinition) continue;
            // Skip if a non-synthesized override exists — source-defined wins.
            var methods = ctx.Registry.GetMethodsForType(type).ToList();
            foreach (RoutineInfo routine in methods)
            {
                if (!routine.IsSynthesized) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                if (methods.Any(r => r.Name == routine.Name && !r.IsSynthesized)) continue;
                switch (type)
                {
                    case EntityTypeInfo entity:
                        HandleEntityGenericDefWired(routine: routine, entity: entity,
                            textType: textType, boolType: boolType, u64Type: u64Type);
                        break;
                    case RecordTypeInfo { HasDirectBackendType: false } record:
                        HandleRecordGenericDefWired(routine: routine, record: record,
                            textType: textType, boolType: boolType,
                            s32Type: s32Type, u64Type: u64Type);
                        break;
                }
            }
        }
    }

    private void HandleEntityGenericDefWired(RoutineInfo routine, EntityTypeInfo entity,
        TypeInfo textType, TypeInfo boolType, TypeInfo? u64Type)
    {
        switch (routine.Name)
        {
            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: false);
                break;
            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: true);
                break;
            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] = entity.MemberVariables.Count == 0
                    ? BuildReturnTrueBody(boolType: boolType)
                    : BuildEqBody(ownerType: entity, fields: entity.MemberVariables,
                        boolType: boolType);
                break;
            case HashMethodName when entity.MemberVariables.Count > 0 && u64Type != null
                                     && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildHashBody(ownerType: entity, fields: entity.MemberVariables,
                        u64Type: u64Type);
                break;
            case HashMethodName when entity.MemberVariables.Count > 0 && u64Type != null
                                     && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSecureHashBody(ownerType: entity, fields: entity.MemberVariables,
                        u64Type: u64Type);
                break;
        }
    }

    private void HandleRecordGenericDefWired(RoutineInfo routine, RecordTypeInfo record,
        TypeInfo textType, TypeInfo boolType, TypeInfo? s32Type, TypeInfo? u64Type)
    {
        switch (routine.Name)
        {
            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: false);
                break;
            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: true);
                break;
            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBody(ownerType: record, fields: record.MemberVariables, boolType: boolType);
                break;
            case HashMethodName when u64Type != null && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildHashBody(ownerType: record, fields: record.MemberVariables, u64Type: u64Type);
                break;
            case HashMethodName when u64Type != null && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSecureHashBody(ownerType: record, fields: record.MemberVariables,
                        u64Type: u64Type);
                break;
            case "$cmp" when s32Type != null:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCmpBody(ownerType: record, fields: record.MemberVariables,
                        s32Type: s32Type, boolType: boolType);
                break;
        }
    }

    //  Per-type handlers

    private void HandleRecord(RoutineInfo routine, RecordTypeInfo record,
        TypeInfo textType, TypeInfo boolType, TypeInfo? s32Type) // NOSONAR S3776
    {
        // Numeric $create bodies for @llvm-typed primitive records.
        // S64.$create(from: Choice) -> sign_extend; U64.$create(from: Flags) -> reinterpret_bits.
        // Must be checked before the HasDirectBackendType guard because these live on S64/U64.
        if (routine is { Name: "$create", Parameters.Count: 1 })
        {
            TypeInfo paramType = routine.Parameters[index: 0].Type;
            string paramName = routine.Parameters[index: 0].Name;
            TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
            TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
            if (paramType is ChoiceTypeInfo && record.Name == "S64" && s64Type != null)
            {
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildLlvmIntrinsicCallBody(intrinsicName: "sign_extend",
                        fromType: paramType, toType: s64Type, paramName: paramName);
                return;
            }
            if (paramType is FlagsTypeInfo && record.Name == "U64" && u64Type != null)
            {
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildLlvmIntrinsicCallBody(intrinsicName: "reinterpret_bits",
                        fromType: paramType, toType: u64Type, paramName: paramName);
                return;
            }
        }

        if (record.HasDirectBackendType) return;

        switch (routine.Name)
        {
            case "$eq":
            {
                // $eq generation requires knowing the concrete field types at body-gen time.
                // Generic definitions are handled per concrete instantiation via GMP.
                if (record.IsGenericDefinition) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBody(ownerType: record, fields: record.MemberVariables, boolType: boolType);
                break;
            }

            case "$cmp":
            {
                if (s32Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCmpBody(ownerType: record, fields: record.MemberVariables,
                        s32Type: s32Type, boolType: boolType);
                break;
            }

            case "$copy":
            {
                // For Assignable records: `return me` lowers to a structural bitwise copy
                // via the return value. Raw-ptr wrappers (Hijacked/CPtr) and records that
                // opt in with a custom $copy body keep their user-written version — this
                // branch only fires when the routine was registered as a synth stub.
                ctx.VariantBodies[key: routine.RegistryKey] = BuildReturnMeBody(ownerType: record);
                break;
            }

            case "clone":
            {
                // Assignable obeys Cloneable: synth clone() as `return me.$copy()`.
                ctx.VariantBodies[key: routine.RegistryKey] = BuildCloneViaCopyBody(ownerType: record);
                break;
            }

            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: false);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: true);
                break;

            case HashMethodName when routine.Parameters.Count == 0:
            {
                // Generic definitions allowed: monomorphization substitutes type params.
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildHashBody(ownerType: record, fields: record.MemberVariables, u64Type: u64Type);
                break;
            }

            case HashMethodName when routine.Parameters.Count == 2:
            {
                if (record.IsGenericDefinition) break;
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSecureHashBody(ownerType: record, fields: record.MemberVariables,
                        u64Type: u64Type);
                break;
            }
        }
    }

    private void HandleEntity(RoutineInfo routine, EntityTypeInfo entity, TypeInfo textType,
        TypeInfo boolType)
    {
        // Generic definitions allowed: monomorphization substitutes type params via _typeSubstitutions.
        switch (routine.Name)
        {
            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: false);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: true);
                break;

            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] = entity.MemberVariables.Count == 0
                    ? BuildReturnTrueBody(boolType: boolType)
                    : BuildEqBody(ownerType: entity, fields: entity.MemberVariables, boolType: boolType);
                break;

            case HashMethodName when entity.MemberVariables.Count > 0
                                     && routine.Parameters.Count == 0:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildHashBody(ownerType: entity, fields: entity.MemberVariables, u64Type: u64Type);
                break;
            }

            case HashMethodName when entity.MemberVariables.Count > 0
                                     && routine.Parameters.Count == 2:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSecureHashBody(ownerType: entity, fields: entity.MemberVariables,
                        u64Type: u64Type);
                break;
            }

            // Text.$create(from: T) -> return from.$represent()
            case "$create" when entity.Name == "Text" && routine.Parameters.Count == 1:
            {
                TypeInfo paramType = routine.Parameters[index: 0].Type;
                string paramName = routine.Parameters[index: 0].Name;
                var fromRef = new IdentifierExpression(Name: paramName, Location: _synthLoc)
                    { ResolvedType = paramType };
                var representCall = new CallExpression(
                    Callee: new MemberExpression(
                        Object: fromRef,
                        PropertyName: RepresentMethodName,
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };
                ctx.VariantBodies[key: routine.RegistryKey] =
                    new ReturnStatement(Value: representCall, Location: _synthLoc);
                break;
            }
        }
    }

    private void HandleCrashable(RoutineInfo routine, CrashableTypeInfo crashable,
        TypeInfo textType)
    {
        switch (routine.Name)
        {
            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableRepresentBody(crashable: crashable);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableDiagnoseBody(crashable: crashable, textType: textType);
                break;

            case "crash_title":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    new ReturnStatement(
                        Value: new LiteralExpression(
                            Value: crashable.CrashTitle,
                            LiteralType: TokenType.TextLiteral,
                            Location: _synthLoc) { ResolvedType = textType },
                        Location: _synthLoc);
                break;
        }
    }

    private void HandleChoice(RoutineInfo routine, ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo boolType, TypeInfo? logicBreachedErrorType, TypeInfo? u64Type, TypeInfo? s64Type,
        TypeInfo? listTypeDef)
    {
        switch (routine.Name)
        {
            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBodyNumeric(ownerType: choice, boolType: boolType, isChoice: true);
                break;

            case HashMethodName when s64Type != null && u64Type != null
                                     && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildNumericHashBodyViaConversion(ownerType: choice, conversionTypeName: "S64",
                        conversionType: s64Type, u64Type: u64Type);
                break;

            case HashMethodName when s64Type != null && u64Type != null
                                     && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildNumericSecureHashBodyViaConversion(ownerType: choice,
                        conversionTypeName: "S64", conversionType: s64Type, u64Type: u64Type);
                break;

            case "all_cases" when listTypeDef != null:
            {
                TypeInfo listChoiceType =
                    ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef,
                        typeArguments: [choice]);
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildAllCasesBody(memberNames: choice.Cases.Select(c => c.Name).ToList(),
                        elementType: choice, listType: listChoiceType);
                break;
            }

            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildChoiceRepresentBody(choice: choice, textType: textType,
                        logicBreachedErrorType: logicBreachedErrorType);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildChoiceDiagnoseBody(choice: choice, textType: textType,
                        logicBreachedErrorType: logicBreachedErrorType);
                break;

            case "$create!":
                // Text -> ChoiceType conversion is not implementable at the RF level;
                // this always crashes. The body is unreachable in well-typed programs.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType);
                break;

            case "$copy":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildReturnMeBody(ownerType: choice);
                break;

            case "clone":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildCloneViaCopyBody(ownerType: choice);
                break;
        }
    }

    /// <summary>
    /// Builds the body: <c>return me == you</c> for choice and flags types.
    /// The <c>BinaryExpression(Equal)</c> lowers to <c>icmp eq i32</c> (choice) or
    /// <c>icmp eq i64</c> (flags) in <c>EmitPrimitiveBinaryOp</c>.
    /// </summary>
    private static ReturnStatement BuildEqBodyNumeric(TypeInfo ownerType, TypeInfo boolType,
        bool isChoice)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var youRef = new IdentifierExpression(Name: "you", Location: _synthLoc)
            { ResolvedType = ownerType };
        // Choice: BinaryOperator.Is -> EmitChoiceIs -> icmp eq i32 (no $eq recursion).
        // Flags: BinaryOperator.Equal stays — OperatorLoweringPass skips it for flags,
        // and codegen emits icmp eq i64 via the flags-specific handler.
        BinaryOperator op = isChoice ? BinaryOperator.Is : BinaryOperator.Equal;
        var cmp = new BinaryExpression(
            Left: meRef,
            Operator: op,
            Right: youRef,
            Location: _synthLoc) { ResolvedType = boolType };
        return new ReturnStatement(Value: cmp, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return me.f1 == you.f1 and me.f2 == you.f2 and ...</c>
    /// Zero-field types: <c>return true</c>.
    /// </summary>
    private static ReturnStatement BuildEqBody(TypeInfo ownerType,
        List<MemberVariableInfo> fields, TypeInfo boolType)
    {
        if (fields.Count == 0)
        {
            return new ReturnStatement(
                Value: new LiteralExpression(
                    Value: true,
                    LiteralType: TokenType.True,
                    Location: _synthLoc) { ResolvedType = boolType },
                Location: _synthLoc);
        }

        Expression? combined = null;
        foreach (MemberVariableInfo field in fields)
        {
            // Blank fields carry no information — two Blank values are always equal; skip them
            // to avoid emitting calls to Blank.$eq (void params, illegal in LLVM IR).
            if (field.Type.IsBlank) continue;

            var lhs = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = ownerType },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var rhs = new MemberExpression(
                Object: new IdentifierExpression(Name: "you", Location: _synthLoc)
                    { ResolvedType = ownerType },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var cmp = new BinaryExpression(
                Left: lhs,
                Operator: BinaryOperator.Equal,
                Right: rhs,
                Location: _synthLoc) { ResolvedType = boolType };

            combined = combined == null
                ? cmp
                : new BinaryExpression(
                    Left: combined,
                    Operator: BinaryOperator.And,
                    Right: cmp,
                    Location: _synthLoc) { ResolvedType = boolType };
        }

        return new ReturnStatement(Value: combined ?? new LiteralExpression(
            Value: true, LiteralType: TokenType.True, Location: _synthLoc)
            { ResolvedType = boolType }, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return true</c> for zero-field entity types.
    /// Zero-field entities have no distinguishing state, so any two instances are structurally equal.
    /// </summary>
    /// <summary>
    /// Builds the body: <c>return me</c>. Used for synthesized <c>$copy()</c> on
    /// Assignable types. Codegen emits a bitwise copy of the receiver into the
    /// return slot — no method dispatch overhead at the call site.
    /// </summary>
    private static ReturnStatement BuildReturnMeBody(TypeInfo ownerType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        return new ReturnStatement(Value: meRef, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return me.$copy()</c>. Used for synthesized <c>clone()</c>
    /// on Assignable types — clone is an Assignable-implied alias for the explicit
    /// copy verb, so it just forwards.
    /// </summary>
    private static ReturnStatement BuildCloneViaCopyBody(TypeInfo ownerType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var copyMember = new MemberExpression(
            Object: meRef,
            PropertyName: "$copy",
            Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var copyCall = new CallExpression(
            Callee: copyMember,
            Arguments: [],
            Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        return new ReturnStatement(Value: copyCall, Location: _synthLoc);
    }

    private static ReturnStatement BuildReturnTrueBody(TypeInfo boolType)
    {
        return new ReturnStatement(
            Value: new LiteralExpression(
                Value: true,
                LiteralType: TokenType.True,
                Location: _synthLoc) { ResolvedType = boolType },
            Location: _synthLoc);
    }

    //  $hash

    /// <summary>
    /// Builds the body: <c>return me.f1.$hash() ^ me.f2.$hash() ^ ...</c>.
    /// Zero-field types: <c>return 0_u64</c>.
    /// </summary>
    private ReturnStatement BuildHashBody(TypeInfo ownerType,
        List<MemberVariableInfo> fields, TypeInfo u64Type)
    {
        if (fields.Count == 0)
        {
            return new ReturnStatement(
                Value: new LiteralExpression(
                    Value: 0UL,
                    LiteralType: TokenType.U64Literal,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Location: _synthLoc);
        }

        // Pre-resolve U64.$bitxor so synthesized CallExpression nodes carry a concrete
        // ResolvedRoutine. Without it, codegen's DirectMemberRoutine path throws when it
        // can't determine the receiver type for the XOR accumulator call.
        RoutineInfo? u64Bitxor = ctx.Registry.LookupMethod(type: u64Type, methodName: BitXorMethodName);

        Expression? accum = null;
        foreach (MemberVariableInfo field in fields)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = ownerType };
            var fieldAccess = new MemberExpression(
                Object: meRef,
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };
            var hashMethod = new MemberExpression(
                Object: fieldAccess,
                PropertyName: HashMethodName,
                Location: _synthLoc) { ResolvedType = u64Type };
            RoutineInfo? fieldHashRoutine = ctx.Registry.LookupMethodOverload(
                type: field.Type, methodName: HashMethodName, argTypes: []);
            Expression fieldHash = new CallExpression(
                Callee: hashMethod,
                Arguments: [],
                Location: _synthLoc)
            {
                ResolvedType = u64Type,
                ResolvedRoutine = fieldHashRoutine,
                LoweringKind = CallLoweringKind.DirectMemberRoutine
            };

            if (accum == null)
            {
                accum = fieldHash;
            }
            else
            {
                // XOR the accumulated hash with this field's hash via $bitxor method call.
                // BinaryExpression(BitwiseXor) must be lowered before codegen; synthesized
                // bodies bypass the lowering pass, so we emit the method call directly.
                accum = new CallExpression(
                    Callee: new MemberExpression(
                        Object: accum,
                        PropertyName: BitXorMethodName,
                        Location: _synthLoc) { ResolvedType = u64Type },
                    Arguments: [new NamedArgumentExpression(Name: "you", Value: fieldHash, Location: _synthLoc)],
                    Location: _synthLoc)
                {
                    ResolvedType = u64Type,
                    ResolvedRoutine = u64Bitxor,
                    LoweringKind = CallLoweringKind.DirectMemberRoutine
                };
            }
        }

        return new ReturnStatement(Value: accum!, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return ConversionType(from: me).$hash()</c>.
    /// Used for Choice (<c>S64(from: me).$hash()</c>) and Flags (<c>U64(from: me).$hash()</c>).
    /// The numeric create lowers via the existing codegen numeric-create path; <c>$hash</c>
    /// on the result delegates to the primitive type's xxHash64 implementation.
    /// </summary>
    private static ReturnStatement BuildNumericHashBodyViaConversion(TypeInfo ownerType,
        string conversionTypeName, TypeInfo conversionType, TypeInfo u64Type)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var creator = new CreatorExpression(
            TypeName: conversionTypeName,
            TypeArguments: null,
            MemberVariables: [("from", meRef)],
            Location: _synthLoc)
        {
            ResolvedType = conversionType,
            LoweringKind = CallLoweringKind.TypeConstructor
        };
        var hashCall = new CallExpression(
            Callee: new MemberExpression(
                Object: creator,
                PropertyName: HashMethodName,
                Location: _synthLoc) { ResolvedType = u64Type },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = u64Type };
        return new ReturnStatement(Value: hashCall, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return [Member1, Member2, ...]</c> as a list literal.
    /// Used for <c>all_cases()</c> on choice and flags types.
    /// </summary>
    private static ReturnStatement BuildAllCasesBody(List<string> memberNames,
        TypeInfo elementType, TypeInfo listType)
    {
        var elements = memberNames
            .Select(name => (Expression)new IdentifierExpression(Name: name, Location: _synthLoc)
                { ResolvedType = elementType })
            .ToList();
        return new ReturnStatement(
            Value: new ListLiteralExpression(
                Elements: elements,
                ElementType: null,
                Location: _synthLoc) { ResolvedType = listType },
            Location: _synthLoc);
    }

    //  Numeric $create (sign_extend / reinterpret_bits)

    /// <summary>
    /// Builds: <c>return intrinsicName[From, To](value: paramName)</c>.
    /// Used for <c>S64.$create(from: Choice)</c> via <c>sign_extend</c> and
    /// <c>U64.$create(from: Flags)</c> via <c>reinterpret_bits</c>.
    /// </summary>
    private static ReturnStatement BuildLlvmIntrinsicCallBody(string intrinsicName,
        TypeInfo fromType, TypeInfo toType, string paramName)
    {
        var fromRef = new IdentifierExpression(Name: paramName, Location: _synthLoc)
            { ResolvedType = fromType };
        var typeArgFrom = new TypeExpression(
            Name: fromType.Name, GenericArguments: null, Location: _synthLoc)
            { ResolvedType = fromType };
        var typeArgTo = new TypeExpression(
            Name: toType.Name, GenericArguments: null, Location: _synthLoc)
            { ResolvedType = toType };
        var call = new CallExpression(
            Callee: new IdentifierExpression(Name: intrinsicName, Location: _synthLoc)
                { ResolvedType = toType },
            Arguments: [new NamedArgumentExpression(Name: "value", Value: fromRef, Location: _synthLoc)],
            Location: _synthLoc)
        {
            ResolvedType = toType,
            LoweringKind = CallLoweringKind.LlvmIntrinsic,
            TypeArguments = [typeArgFrom, typeArgTo]
        };
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    //  keyed $hash(k0, k1)

    /// <summary>
    /// Builds the body: <c>return me.f1.$hash(k0: k0, k1: k1) ^ me.f2.$hash(...) ^ ...</c>.
    /// Zero-field types: <c>return 0_u64</c>.
    /// </summary>
    private ReturnStatement BuildSecureHashBody(TypeInfo ownerType,
        List<MemberVariableInfo> fields, TypeInfo u64Type)
    {
        if (fields.Count == 0)
            return new ReturnStatement(
                Value: new LiteralExpression(Value: 0UL, LiteralType: TokenType.U64Literal,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Location: _synthLoc);

        RoutineInfo? u64Bitxor = ctx.Registry.LookupMethod(type: u64Type, methodName: BitXorMethodName);

        var k0Ref = new IdentifierExpression(Name: "k0", Location: _synthLoc) { ResolvedType = u64Type };
        var k1Ref = new IdentifierExpression(Name: "k1", Location: _synthLoc) { ResolvedType = u64Type };

        Expression? accum = null;
        foreach (MemberVariableInfo field in fields)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = ownerType };
            var fieldAccess = new MemberExpression(Object: meRef, PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };
            RoutineInfo? fieldSecureHashRoutine = ctx.Registry.LookupMethodOverload(
                type: field.Type, methodName: HashMethodName, argTypes: [u64Type, u64Type]);
            Expression fieldHash = new CallExpression(
                Callee: new MemberExpression(Object: fieldAccess, PropertyName: HashMethodName,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Arguments:
                [
                    new NamedArgumentExpression(Name: "k0", Value: k0Ref, Location: _synthLoc),
                    new NamedArgumentExpression(Name: "k1", Value: k1Ref, Location: _synthLoc)
                ],
                Location: _synthLoc)
            {
                ResolvedType = u64Type,
                ResolvedRoutine = fieldSecureHashRoutine,
                LoweringKind = CallLoweringKind.DirectMemberRoutine
            };

            if (accum == null)
            {
                accum = fieldHash;
            }
            else
            {
                accum = new CallExpression(
                    Callee: new MemberExpression(
                        Object: accum,
                        PropertyName: BitXorMethodName,
                        Location: _synthLoc) { ResolvedType = u64Type },
                    Arguments: [new NamedArgumentExpression(Name: "you", Value: fieldHash, Location: _synthLoc)],
                    Location: _synthLoc)
                {
                    ResolvedType = u64Type,
                    ResolvedRoutine = u64Bitxor,
                    LoweringKind = CallLoweringKind.DirectMemberRoutine
                };
            }
        }

        return new ReturnStatement(Value: accum!, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return ConversionType(from: me).$hash(k0: k0, k1: k1)</c>.
    /// Used for Choice (<c>S64(from: me)</c>) and Flags (<c>U64(from: me)</c>).
    /// </summary>
    private static ReturnStatement BuildNumericSecureHashBodyViaConversion(TypeInfo ownerType,
        string conversionTypeName, TypeInfo conversionType, TypeInfo u64Type)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var k0Ref = new IdentifierExpression(Name: "k0", Location: _synthLoc) { ResolvedType = u64Type };
        var k1Ref = new IdentifierExpression(Name: "k1", Location: _synthLoc) { ResolvedType = u64Type };
        var creator = new CreatorExpression(TypeName: conversionTypeName, TypeArguments: null,
            MemberVariables: [("from", meRef)], Location: _synthLoc)
        {
            ResolvedType = conversionType,
            LoweringKind = CallLoweringKind.TypeConstructor
        };
        var hashCall = new CallExpression(
            Callee: new MemberExpression(Object: creator, PropertyName: HashMethodName,
                Location: _synthLoc) { ResolvedType = u64Type },
            Arguments:
            [
                new NamedArgumentExpression(Name: "k0", Value: k0Ref, Location: _synthLoc),
                new NamedArgumentExpression(Name: "k1", Value: k1Ref, Location: _synthLoc)
            ],
            Location: _synthLoc) { ResolvedType = u64Type };
        return new ReturnStatement(Value: hashCall, Location: _synthLoc);
    }

    //  $cmp

    /// <summary>
    /// Builds the body: lexicographic field comparison returning S32 (-1/0/1).
    /// <c>var r = me.f1.$cmp(you: you.f1); if r != 0 { return r } ...</c>
    /// Zero-field types: <c>return 0_s32</c>.
    /// </summary>
    private static Statement BuildCmpBody(TypeInfo ownerType,
        List<MemberVariableInfo> fields, TypeInfo s32Type, TypeInfo boolType)
    {
        var zeroS32 = new LiteralExpression(
            Value: 0L,
            LiteralType: TokenType.S32Literal,
            Location: _synthLoc) { ResolvedType = s32Type };

        if (fields.Count == 0)
            return new ReturnStatement(Value: zeroS32, Location: _synthLoc);

        var stmts = new List<Statement>(capacity: fields.Count * 2 + 1);
        bool first = true;

        foreach (MemberVariableInfo field in fields)
        {
            var meField = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = ownerType },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var youField = new MemberExpression(
                Object: new IdentifierExpression(Name: "you", Location: _synthLoc)
                    { ResolvedType = ownerType },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var cmpCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: meField,
                    PropertyName: "$cmp",
                    Location: _synthLoc) { ResolvedType = s32Type },
                Arguments:
                [
                    new NamedArgumentExpression(
                        Name: "you",
                        Value: youField,
                        Location: _synthLoc)
                ],
                Location: _synthLoc) { ResolvedType = s32Type };

            if (first)
            {
                stmts.Add(new DeclarationStatement(
                    Declaration: new VariableDeclaration(
                        Name: "r",
                        Type: null,
                        Initializer: cmpCall,
                        Visibility: VisibilityModifier.Open,
                        Location: _synthLoc),
                    Location: _synthLoc));
                first = false;
            }
            else
            {
                stmts.Add(new AssignmentStatement(
                    Target: new IdentifierExpression(Name: "r", Location: _synthLoc)
                        { ResolvedType = s32Type },
                    Value: cmpCall,
                    Location: _synthLoc));
            }

            var isNonZero = new BinaryExpression(
                Left: new IdentifierExpression(Name: "r", Location: _synthLoc)
                    { ResolvedType = s32Type },
                Operator: BinaryOperator.NotEqual,
                Right: new LiteralExpression(
                    Value: 0L,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc) { ResolvedType = s32Type },
                Location: _synthLoc) { ResolvedType = boolType };

            stmts.Add(new IfStatement(
                Condition: isNonZero,
                ThenStatement: new ReturnStatement(
                    Value: new IdentifierExpression(Name: "r", Location: _synthLoc)
                        { ResolvedType = s32Type },
                    Location: _synthLoc),
                ElseStatement: null,
                Location: _synthLoc));
        }

        stmts.Add(new ReturnStatement(Value: zeroS32, Location: _synthLoc));
        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    //  $represent / $diagnose (record + entity)

    /// <summary>
    /// Builds the body for <c>$represent</c> or <c>$diagnose</c> on a record or entity.
    /// <list type="bullet">
    ///   <item><c>$represent</c>: <c>return f"TypeName(f1: {me.f1}, f2: {me.f2})"</c> -> open+posted fields, named.</item>
    ///   <item><c>$diagnose</c>:  <c>return f"Module.TypeName(f1: {me.f1}, [secret] f2: {me.f2})"</c> -> all fields named,
    ///         values via <c>$represent</c> (not <c>$diagnose</c>) to avoid cascading verbosity.</item>
    /// </list>
    /// Field access via <see cref="MemberExpression"/> works for both records (extractvalue) and
    /// entities (GEP + load).
    /// </summary>
    private static ReturnStatement BuildTextBody(
        TypeInfo ownerType,
        List<MemberVariableInfo> fields,
        TypeInfo textType,
        bool diagnose)
    {
        var parts = new List<InsertedTextPart>();

        // Emit `me.type_name()` (or `me.full_type_name()` for diagnose) so per-instance
        // monomorphization produces the correct generic-args-included name (e.g.
        // "List[Core.S64]"). Baking ownerType.Name/FullName here freezes the generic-def
        // name ("List") and the type-args are lost in monomorphized bodies.
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var typeNameCall = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: diagnose ? "full_type_name" : "type_name",
                Location: _synthLoc) { ResolvedType = textType },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = textType };
        parts.Add(new ExpressionPart(
            Expression: typeNameCall, FormatSpec: null, Location: _synthLoc));
        parts.Add(new TextPart(Text: "(", Location: _synthLoc));

        IEnumerable<MemberVariableInfo> visibleFields = diagnose
            ? fields
            : fields.Where(predicate: f =>
                f.Visibility is VisibilityModifier.Open or VisibilityModifier.Posted);

        bool first = true;
        foreach (MemberVariableInfo field in visibleFields)
        {
            if (!first)
                parts.Add(new TextPart(Text: ", ", Location: _synthLoc));
            first = false;

            string secretPrefix = diagnose && field.Visibility == VisibilityModifier.Secret
                ? "[secret] "
                : "";
            parts.Add(new TextPart(
                Text: secretPrefix + field.Name + ": ",
                Location: _synthLoc));

            var fieldExpr = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = ownerType },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            // Always use $represent for field values, even inside $diagnose.
            // Using $diagnose recursively would produce exponentially verbose output.
            parts.Add(new ExpressionPart(
                Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring = new InsertedTextExpression(
            Parts: parts,
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    //  $represent / $diagnose (choice)

    /// <summary>
    /// Builds the body: a WhenStatement over <c>me</c> returning the case name string.
    /// </summary>
    private static WhenStatement BuildChoiceRepresentBody(ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo? logicBreachedErrorType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = choice };

        var clauses = new List<WhenClause>(capacity: choice.Cases.Count + 1);
        foreach (ChoiceCaseInfo c in choice.Cases)
        {
            clauses.Add(new WhenClause(
                Pattern: new LiteralPattern(
                    Value: c.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc),
                Body: new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: c.Name, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                        { ResolvedType = textType },
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: a WhenStatement over <c>me</c> returning
    /// <c>"Module.ChoiceName(id: N, CaseName)"</c> per case.
    /// </summary>
    private static WhenStatement BuildChoiceDiagnoseBody(ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo? logicBreachedErrorType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = choice };

        string prefix = choice.FullName + "(id: ";
        var clauses = new List<WhenClause>(capacity: choice.Cases.Count + 1);
        foreach (ChoiceCaseInfo c in choice.Cases)
        {
            string text = $"{prefix}{c.ComputedValue}, {c.Name})";
            clauses.Add(new WhenClause(
                Pattern: new LiteralPattern(
                    Value: c.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc),
                Body: new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: text, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                        { ResolvedType = textType },
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    //  $represent / $diagnose (flags)

    private void HandleFlags(RoutineInfo routine, FlagsTypeInfo flags,
        TypeInfo textType, TypeInfo boolType, TypeInfo? u64Type, TypeInfo? listTypeDef)
    {
        switch (routine.Name)
        {
            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBodyNumeric(ownerType: flags, boolType: boolType, isChoice: false);
                break;

            case HashMethodName when u64Type != null && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildNumericHashBodyViaConversion(ownerType: flags, conversionTypeName: "U64",
                        conversionType: u64Type, u64Type: u64Type);
                break;

            case HashMethodName when u64Type != null && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildNumericSecureHashBodyViaConversion(ownerType: flags,
                        conversionTypeName: "U64", conversionType: u64Type, u64Type: u64Type);
                break;

            case "all_cases" when listTypeDef != null:
            {
                TypeInfo listFlagsType =
                    ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef,
                        typeArguments: [flags]);
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildAllCasesBody(memberNames: flags.Members.Select(m => m.Name).ToList(),
                        elementType: flags, listType: listFlagsType);
                break;
            }

            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildFlagsRepresentBody(flags: flags, textType: textType, boolType: boolType);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildFlagsDiagnoseBody(flags: flags, textType: textType, boolType: boolType);
                break;

            case "all_off":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: 0L, returnType: routine.ReturnType ?? flags);
                break;

            case "all_on":
            {
                ulong mask = 0;
                foreach (FlagsMemberInfo member in flags.Members)
                    mask |= 1UL << member.BitPosition;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: unchecked((long)mask), returnType: routine.ReturnType ?? flags);
                break;
            }

            case "$copy":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildReturnMeBody(ownerType: flags);
                break;

            case "clone":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildCloneViaCopyBody(ownerType: flags);
                break;
        }
    }

    /// <summary>
    /// Builds the common block of statements that computes:
    /// <list type="bullet">
    ///   <item><c>var result: Text = ""</c> -> flag names joined by <c>" and "</c>.</item>
    ///   <item><c>var first: Bool = true</c> -> separator sentinel.</item>
    ///   <item>When <paramref name="computeBits"/> is <c>true</c>: <c>var bits: Text = "%"</c> ??
    ///         binary string in declaration order, e.g. <c>"%110"</c>.</item>
    ///   <item>For each flag in declaration order:
    ///     <c>if (me &amp; mask) != 0 { /* append name to result; first = false; [append "1" to bits] */ }
    ///     else { [append "0" to bits] }</c></item>
    ///   <item><c>if first { result = "&lt;none&gt;" }</c></item>
    /// </list>
    /// Returns the statement list (no trailing <c>return</c>).
    /// </summary>
    private static List<Statement> BuildFlagsComputeBlock(
        FlagsTypeInfo flags, TypeInfo textType, TypeInfo boolType, TypeInfo s64Type,
        bool computeBits)
    {
        var stmts = new List<Statement>();
        var emptyText = new LiteralExpression(
            Value: "", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };
        var trueLit = new LiteralExpression(
            Value: true, LiteralType: TokenType.True, Location: _synthLoc)
            { ResolvedType = boolType };
        var falseLit = new LiteralExpression(
            Value: false, LiteralType: TokenType.False, Location: _synthLoc)
            { ResolvedType = boolType };
        var zeroLit = new LiteralExpression(
            Value: 0L, LiteralType: TokenType.S64Literal, Location: _synthLoc)
            { ResolvedType = s64Type };
        var noneLit = new LiteralExpression(
            Value: "<none>", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };
        var oneLit = new LiteralExpression(
            Value: "1", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };
        var zeroCharLit = new LiteralExpression(
            Value: "0", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };

        // var result: Text = ""
        stmts.Add(new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: ResultVarName,
                Type: null,
                Initializer: emptyText,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc));

        // var first: Bool = true
        stmts.Add(new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: FirstVarName,
                Type: null,
                Initializer: trueLit,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc));

        if (computeBits)
        {
            // var bits: Text = "%"
            stmts.Add(new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: "bits",
                    Type: null,
                    Initializer: new LiteralExpression(
                        Value: "%", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                        { ResolvedType = textType },
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = flags };

        foreach (FlagsMemberInfo member in flags.Members)
        {
            long mask = 1L << member.BitPosition;
            var maskLit = new LiteralExpression(
                Value: mask, LiteralType: TokenType.S64Literal, Location: _synthLoc)
                { ResolvedType = s64Type };

            // (me & mask) != 0
            var bwAnd = new BinaryExpression(
                Left: meRef,
                Operator: BinaryOperator.BitwiseAnd,
                Right: maskLit,
                Location: _synthLoc) { ResolvedType = s64Type };
            var isSet = new BinaryExpression(
                Left: bwAnd,
                Operator: BinaryOperator.NotEqual,
                Right: zeroLit,
                Location: _synthLoc) { ResolvedType = boolType };

            var nameLit = new LiteralExpression(
                Value: member.Name, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                { ResolvedType = textType };
            var andNameLit = new LiteralExpression(
                Value: " and " + member.Name,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType };

            // result.$add(other: " and FlagName")
            var appendNameCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                        { ResolvedType = textType },
                    PropertyName: "$add",
                    Location: _synthLoc),
                Arguments:
                [
                    new NamedArgumentExpression(
                        Name: OtherParamName,
                        Value: andNameLit,
                        Location: _synthLoc)
                ],
                Location: _synthLoc) { ResolvedType = textType };

            // if first { result = "FlagName"; first = false } else { result = result.$add(...) }
            var innerNameIf = new IfStatement(
                Condition: new IdentifierExpression(Name: FirstVarName, Location: _synthLoc)
                    { ResolvedType = boolType },
                ThenStatement: new BlockStatement(
                    Statements:
                    [
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                                { ResolvedType = textType },
                            Value: nameLit,
                            Location: _synthLoc),
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: FirstVarName, Location: _synthLoc)
                                { ResolvedType = boolType },
                            Value: falseLit,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc),
                ElseStatement: new BlockStatement(
                    Statements:
                    [
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                                { ResolvedType = textType },
                            Value: appendNameCall,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc),
                Location: _synthLoc);

            if (!computeBits)
            {
                // if (me & mask) != 0 { <name logic> }
                stmts.Add(new IfStatement(
                    Condition: isSet,
                    ThenStatement: new BlockStatement(
                        Statements: [innerNameIf],
                        Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc));
            }
            else
            {
                // bits.$add(other: "1") -> set branch
                var append1 = new CallExpression(
                    Callee: new MemberExpression(
                        Object: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                            { ResolvedType = textType },
                        PropertyName: "$add",
                        Location: _synthLoc),
                    Arguments:
                    [
                        new NamedArgumentExpression(
                            Name: OtherParamName, Value: oneLit, Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = textType };

                // bits.$add(other: "0") -> clear branch
                var append0 = new CallExpression(
                    Callee: new MemberExpression(
                        Object: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                            { ResolvedType = textType },
                        PropertyName: "$add",
                        Location: _synthLoc),
                    Arguments:
                    [
                        new NamedArgumentExpression(
                            Name: OtherParamName, Value: zeroCharLit, Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = textType };

                // if (me & mask) != 0 { <name logic>; bits = bits.$add("1") }
                // else               { bits = bits.$add("0") }
                stmts.Add(new IfStatement(
                    Condition: isSet,
                    ThenStatement: new BlockStatement(
                        Statements:
                        [
                            innerNameIf,
                            new AssignmentStatement(
                                Target: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                                    { ResolvedType = textType },
                                Value: append1,
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc),
                    ElseStatement: new BlockStatement(
                        Statements:
                        [
                            new AssignmentStatement(
                                Target: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                                    { ResolvedType = textType },
                                Value: append0,
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc),
                    Location: _synthLoc));
            }
        }

        // if first { result = "<none>" }
        stmts.Add(new IfStatement(
            Condition: new IdentifierExpression(Name: FirstVarName, Location: _synthLoc)
                { ResolvedType = boolType },
            ThenStatement: new BlockStatement(
                Statements:
                [
                    new AssignmentStatement(
                        Target: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                            { ResolvedType = textType },
                        Value: noneLit,
                        Location: _synthLoc)
                ],
                Location: _synthLoc),
            ElseStatement: null,
            Location: _synthLoc));

        return stmts;
    }

    /// <summary>
    /// Builds the <c>$represent</c> body for a flags type.
    /// Returns <c>"Flag1 and Flag2"</c>, or <c>"&lt;none&gt;"</c> if no bits are set.
    /// </summary>
    private Statement BuildFlagsRepresentBody(
        FlagsTypeInfo flags, TypeInfo textType, TypeInfo boolType)
    {
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        if (s64Type == null) return new ReturnStatement(
            Value: new LiteralExpression(
                Value: "<none>", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                { ResolvedType = textType },
            Location: _synthLoc);

        List<Statement> stmts = BuildFlagsComputeBlock(
            flags: flags, textType: textType, boolType: boolType, s64Type: s64Type,
            computeBits: false);

        stmts.Add(new ReturnStatement(
            Value: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                { ResolvedType = textType },
            Location: _synthLoc));

        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the <c>$diagnose</c> body for a flags type.
    /// Returns <c>"Module.FlagsName(value: %110, Flag1 and Flag2)"</c> where the binary string
    /// is in declaration order (<c>%</c> prefix, leftmost = first declared flag).
    /// </summary>
    private Statement BuildFlagsDiagnoseBody(
        FlagsTypeInfo flags, TypeInfo textType, TypeInfo boolType)
    {
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        if (s64Type == null) return new ReturnStatement(
            Value: new LiteralExpression(
                Value: flags.FullName + "(value: %0, <none>)",
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType },
            Location: _synthLoc);

        List<Statement> stmts = BuildFlagsComputeBlock(
            flags: flags, textType: textType, boolType: boolType, s64Type: s64Type,
            computeBits: true);

        // return f"Module.FlagsName(value: {bits}, {result})"
        // Both result and bits are Text -> EmitRepresentCall returns them directly.
        var resultRef = new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
            { ResolvedType = textType };
        var bitsRef = new IdentifierExpression(Name: "bits", Location: _synthLoc)
            { ResolvedType = textType };
        var fstring = new InsertedTextExpression(
            Parts:
            [
                new TextPart(Text: flags.FullName + "(value: ", Location: _synthLoc),
                new ExpressionPart(Expression: bitsRef, FormatSpec: null, Location: _synthLoc),
                new TextPart(Text: ", ", Location: _synthLoc),
                new ExpressionPart(Expression: resultRef, FormatSpec: null, Location: _synthLoc),
                new TextPart(Text: ")", Location: _synthLoc)
            ],
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        stmts.Add(new ReturnStatement(Value: fstring, Location: _synthLoc));

        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    //  Unreachable helper

    /// <summary>
    /// Builds <c>throw LogicBreachedError()</c> for provably-unreachable else arms
    /// (e.g. the default clause in a synthesized choice <c>$represent</c> body).
    /// Falls back to <c>throw LogicBreachedError()</c> with a null ResolvedType when the
    /// type isn't in the registry yet (shouldn't happen in practice).
    /// </summary>
    private static ThrowStatement BuildBreachStatement(TypeInfo? logicBreachedErrorType)
    {
        // Use CallExpression, not CreatorExpression -> crashable constructors in RF source
        // parse as calls (e.g. LogicBreachedError()), and EmitFunctionCall has the
        // crashable-construction path; EmitConstructorCall does not.
        var call = new CallExpression(
            Callee: new IdentifierExpression(Name: "LogicBreachedError", Location: _synthLoc)
                { ResolvedType = logicBreachedErrorType },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = logicBreachedErrorType };
        return new ThrowStatement(Error: call, Location: _synthLoc);
    }

    //  $represent / $diagnose (crashable)

    /// <summary>
    /// Builds the body: <c>return me.crash_message()</c>.
    /// </summary>
    private static ReturnStatement BuildCrashableRepresentBody(CrashableTypeInfo crashable)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = crashable };
        var call = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: "crash_message",
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body:
    /// <c>return f"Module.CrashableName({me.crash_message()}[, field1: {me.f1}, ...])"</c>.
    /// </summary>
    private static ReturnStatement BuildCrashableDiagnoseBody(
        CrashableTypeInfo crashable, TypeInfo textType)
    {
        var parts = new List<InsertedTextPart>();

        // Open with "Module.TypeName("
        parts.Add(new TextPart(Text: crashable.FullName + "(", Location: _synthLoc));

        // First element: crash_message() -> use $represent format (no "?")
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = crashable };
        var crashMsgCall = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: "crash_message",
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        parts.Add(new ExpressionPart(
            Expression: crashMsgCall,
            FormatSpec: null,
            Location: _synthLoc));

        // Remaining member-variable fields
        foreach (MemberVariableInfo field in crashable.MemberVariables)
        {
            parts.Add(new TextPart(Text: ", " + field.Name + ": ", Location: _synthLoc));

            var meRef2 = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = crashable };
            var fieldExpr = new MemberExpression(
                Object: meRef2,
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            parts.Add(new ExpressionPart(
                Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring = new InsertedTextExpression(
            Parts: parts,
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    //  BuilderService constant routines

    /// <summary>
    /// Synthesizes AST bodies for BuilderService routines that return a single compile-time
    /// constant value (Text, U64, S64, Bool). Called before the owner-type switch so it handles
    /// all types uniformly.
    /// Returns <c>true</c> if the routine was handled, <c>false</c> otherwise.
    /// </summary>
    private bool TryHandleBuilderServiceConstant(RoutineInfo routine,
        TypeInfo textType, TypeInfo? u64Type, TypeInfo? s64Type, TypeInfo? boolType,
        TypeInfo? typeKindType, TypeInfo? listTextType, TypeInfo? byteSizeType = null) // NOSONAR S3776
    {
        if (routine.OwnerType == null) return false;
        TypeInfo owner = routine.OwnerType;

        // Skip compiler-internal/non-synthesizable categories.
        if (owner.Category is TypeCategory.TypeParameter or TypeCategory.Error
            or TypeCategory.ProtocolSelf
            or TypeCategory.ConstGenericValue)
            return false;

        switch (routine.Name)
        {
            case "type_name":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.ShortTypeName, returnType: textType);
                return true;

            case "module_name":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.Module ?? "", returnType: textType);
                return true;

            case "full_type_name":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.QualifiedTypeName, returnType: textType);
                return true;

            case "type_id" when u64Type != null:
            {
                ulong hash = TypeIdHelper.ComputeTypeId(fullName: owner.FullName);
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: hash, returnType: u64Type);
                return true;
            }

            case "data_size" when byteSizeType != null && u64Type != null:
            {
                ulong size = CalculateDataSizeForType(type: owner);
                ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
                    Value: BuilderServiceInliningPass.MakeByteSizeCreatorPublic(
                        value: size, u64Type: u64Type, byteSizeType: byteSizeType, loc: _synthLoc),
                    Location: _synthLoc);
                return true;
            }

            case "member_variable_count" when s64Type != null:
            {
                long count = owner switch
                {
                    TupleTypeInfo t => t.MemberVariables.Count,
                    ChoiceTypeInfo ch => ch.Cases.Count,
                    FlagsTypeInfo f => f.Members.Count,
                    RecordTypeInfo r => r.MemberVariables.Count,
                    EntityTypeInfo e => e.MemberVariables.Count,
                    CrashableTypeInfo c => c.MemberVariables.Count,
                    VariantTypeInfo v => v.Members.Count,
                    _ => 0
                };
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: count, returnType: s64Type);
                return true;
            }

            case "member_type_id" when u64Type != null && boolType != null:
            {
                List<MemberVariableInfo>? fields = owner switch
                {
                    RecordTypeInfo r => r.MemberVariables,
                    EntityTypeInfo e => e.MemberVariables,
                    CrashableTypeInfo c => c.MemberVariables,
                    _ => null
                };
                fields ??= [];

                // Build if-elseif chain from last field to first, wrapping each around the
                // previous so the outermost IfStatement checks field[0].
                Statement body = MakeLiteralReturn(value: 0L, returnType: u64Type);
                var memberNameRef = new IdentifierExpression(Name: "member_name", Location: _synthLoc)
                    { ResolvedType = textType };

                for (int i = fields.Count - 1; i >= 0; i--)
                {
                    MemberVariableInfo field = fields[i];
                    ulong typeId = TypeIdHelper.ComputeTypeId(fullName: field.Type.FullName);

                    Expression cond = new CallExpression(
                        Callee: new MemberExpression(
                            Object: memberNameRef,
                            PropertyName: "$eq",
                            Location: _synthLoc),
                        Arguments: [
                            new NamedArgumentExpression(
                                Name: OtherParamName,
                                Value: new LiteralExpression(
                                    Value: field.Name,
                                    LiteralType: TokenType.TextLiteral,
                                    Location: _synthLoc) { ResolvedType = textType },
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc) { ResolvedType = boolType };

                    body = new IfStatement(
                        Condition: cond,
                        ThenStatement: new ReturnStatement(
                            Value: new LiteralExpression(
                                Value: typeId,
                                LiteralType: TokenType.U64Literal,
                                Location: _synthLoc) { ResolvedType = u64Type },
                            Location: _synthLoc),
                        ElseStatement: body,
                        Location: _synthLoc);
                }

                ctx.VariantBodies[key: routine.RegistryKey] = body;
                return true;
            }

            case "is_generic" when boolType != null:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.IsGenericDefinition, returnType: boolType);
                return true;

            case "is_in_flight" when boolType != null:
                // Synth body is only reached for bound `me` receivers (BSInliningPass folds
                // literal/in-flight receivers at the call site). Bound values are never in-flight.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: false, returnType: boolType);
                return true;

            case "type_kind" when typeKindType is ChoiceTypeInfo typeKindChoice:
            {
                // Map the owner's category to the TypeKind case name, then look up its
                // ComputedValue from the registry -> avoids hardcoding ordinals that could
                // drift out of sync with the BuilderService.rf TypeKind declaration.
                string caseName = owner.Category switch
                {
                    TypeCategory.Record => "RECORD",
                    TypeCategory.Entity => "ENTITY",
                    TypeCategory.Crashable => "CRASHABLE",
                    TypeCategory.Choice => "CHOICE",
                    TypeCategory.Variant => "VARIANT",
                    TypeCategory.Flags => "FLAGS",
                    TypeCategory.Routine => "ROUTINE",
                    TypeCategory.Protocol => "PROTOCOL",
                    _ => throw new InvalidOperationException(
                        $"Unhandled TypeCategory '{owner.Category}' in type_kind BuilderService mapping.")
                };
                ChoiceCaseInfo? found =
                    typeKindChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                if (found == null) return false;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: found.ComputedValue, returnType: typeKindChoice);
                return true;
            }

            case "protocols" when listTextType != null:
            {
                List<string> names = owner switch
                {
                    RecordTypeInfo r => r.ImplementedProtocols.Select(p => p.Name).ToList(),
                    EntityTypeInfo e => e.ImplementedProtocols.Select(p => p.Name).ToList(),
                    CrashableTypeInfo c => c.ImplementedProtocols.Select(p => p.Name).ToList(),
                    _ => []
                };
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: names, textType: textType, listTextType: listTextType);
                return true;
            }

            case "routine_names" when listTextType != null:
            {
                var names = ctx.Registry.GetMethodsForType(type: owner)
                               .Select(r => r.Name).Distinct().ToList();
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: names, textType: textType, listTextType: listTextType);
                return true;
            }

            case "generic_args" when listTextType != null:
            {
                List<string> args = owner.TypeArguments?.Select(t => t.Name).ToList()
                    ?? owner.GenericParameters?.ToList() ?? [];
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: args, textType: textType, listTextType: listTextType);
                return true;
            }

            case "annotations" when listTextType != null:
                // Type-level annotations are not yet tracked on TypeInfo -> return empty list
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "dependencies" when listTextType != null:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "protocol_info" when listTextType != null:
                // Full ProtocolInfo entity allocation deferred -> return empty list
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "routine_info" when listTextType != null:
                // TODO: not yet implemented — full RoutineInfo entity allocation deferred; returns empty list
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "member_variable_info"
                when owner is RecordTypeInfo or EntityTypeInfo or CrashableTypeInfo:
            {
                TypeInfo? fieldInfoType = ctx.Registry.LookupType(name: "FieldInfo");
                TypeInfo? ownedDef = ctx.Registry.LookupType(name: "Owned");
                TypeInfo? listDef = ctx.Registry.LookupType(name: "List");
                if (fieldInfoType == null || ownedDef == null || listDef == null) return false;
                TypeInfo ownedFieldInfo = ctx.Registry.GetOrCreateResolution(
                    genericDef: ownedDef, typeArguments: [fieldInfoType]);
                TypeInfo listOwnedFieldInfo = ctx.Registry.GetOrCreateResolution(
                    genericDef: listDef, typeArguments: [ownedFieldInfo]);
                ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
                    Value: new ListLiteralExpression(
                        Elements: [],
                        ElementType: null,
                        Location: _synthLoc)
                        { ResolvedType = listOwnedFieldInfo },
                    Location: _synthLoc);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles standalone (non-owner) BuilderService constants derived from
    /// <see cref="DesugaringContext.Target"/> / <see cref="DesugaringContext.BuildMode"/>.
    /// </summary>
    private bool TryHandleStandaloneBuilderServiceConstant(RoutineInfo routine,
        TypeInfo textType, TypeInfo? u64Type, TypeInfo? byteSizeType)
    {
        switch (routine.Name)
        {
            case "page_size":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)ctx.Target.PageSize,
                    u64Type: u64Type, byteSizeType: byteSizeType);

            case "cache_line":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)ctx.Target.CacheLineSize,
                    u64Type: u64Type, byteSizeType: byteSizeType);

            case "word_size":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)(ctx.Target.PointerBitWidth / 8),
                    u64Type: u64Type, byteSizeType: byteSizeType);

            case "target_os":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: ctx.Target.TargetOS, returnType: textType);
                return true;

            case "target_arch":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: ctx.Target.TargetArch, returnType: textType);
                return true;

            case "builder_version":
            {
                Version version = typeof(WiredRoutinePass).Assembly.GetName().Version
                    ?? throw new InvalidOperationException(
                        "Unable to resolve the RazorForge assembly version for builder_version().");
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: version.ToString(fieldCount: 3), returnType: textType);
                return true;
            }

            case "build_timestamp":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: DateTime.UtcNow.ToString(format: "o"),
                        returnType: textType);
                return true;

            case "build_mode":
            {
                if (routine.ReturnType is ChoiceTypeInfo buildModeChoice)
                {
                    string caseName = ctx.BuildMode switch
                    {
                        RfBuildMode.Debug => "DEBUG",
                        RfBuildMode.Release => "RELEASE",
                        RfBuildMode.ReleaseTime => "RELEASE_TIME",
                        RfBuildMode.ReleaseSpace => "RELEASE_SPACE",
                        _ => throw new InvalidOperationException(
                            $"Unhandled RfBuildMode value '{ctx.BuildMode}'.")
                    };
                    ChoiceCaseInfo? found =
                        buildModeChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                    if (found == null) return false;
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        MakeLiteralReturn(value: found.ComputedValue,
                            returnType: buildModeChoice);
                    return true;
                }
                return false;
            }

            default:
                return false;
        }
    }

    private bool EmitByteSizeOrU64(RoutineInfo routine, ulong value,
        TypeInfo? u64Type, TypeInfo? byteSizeType)
    {
        if (byteSizeType == null || u64Type == null)
        {
            return false;
        }

        ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
            Value: BuilderServiceInliningPass.MakeByteSizeCreatorPublic(
                value: value, u64Type: u64Type, byteSizeType: byteSizeType, loc: _synthLoc),
            Location: _synthLoc);
        return true;
    }

    /// <summary>
    /// Builds a <c>return [elem0, elem1, ...]</c> statement using a
    /// <see cref="ListLiteralExpression"/> with the given Text string values.
    /// </summary>
    private static ReturnStatement MakeListReturn(List<string> values,
        TypeInfo textType, TypeInfo listTextType)
    {
        var elements = values
            .Select(v => (Expression)new LiteralExpression(
                Value: v,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType })
            .ToList();
        return new ReturnStatement(
            Value: new ListLiteralExpression(
                Elements: elements,
                ElementType: null,
                Location: _synthLoc) { ResolvedType = listTextType },
            Location: _synthLoc);
    }

    /// <summary>
    /// Builds the auto-derived <c>$destroy()</c> body. Composite record/entity/crashable types
    /// recurse into their owned fields (<c>me.field.$destroy()</c> for each); scalar kinds
    /// (choices, flags, <c>@llvm</c>-backed primitives, tuples, variants) get a no-op return.
    /// Leaf RC/ptr teardown (Hijacked, Retained/Tracked, Viewed/Grasped) lives in hand-written
    /// wrapper destructors and is never reached here (those types keep their own <c>$destroy</c>).
    /// </summary>
    private Statement BuildDestroyBody(TypeInfo? owner)
    {
        var noop = new ReturnStatement(Value: null, Location: _synthLoc);

        // Variants tear down the *active* arm only: pattern-match the tag and `$destroy` the
        // bound payload. None/void arms (and any non-resource arms) fall through the else no-op.
        if (owner is VariantTypeInfo variant)
            return BuildVariantDestroyBody(variant: variant);

        List<MemberVariableInfo>? fields = owner switch
        {
            EntityTypeInfo e => e.MemberVariables,
            CrashableTypeInfo c => c.MemberVariables,
            // Choices/flags/tuples are RecordTypeInfo subclasses with no owned references —
            // exclude them; only plain composite records (no @llvm backend) recurse.
            ChoiceTypeInfo or FlagsTypeInfo or TupleTypeInfo => null,
            RecordTypeInfo { HasDirectBackendType: false } r => r.MemberVariables,
            _ => null
        };
        if (fields is null or { Count: 0 })
            return noop;

        TypeInfo? blankType = ctx.Registry.LookupType(name: "Blank");
        var statements = new List<Statement>(capacity: fields.Count + 1);
        foreach (MemberVariableInfo field in fields)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = owner };
            var fieldRef = new MemberExpression(Object: meRef, PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };
            var destroyCall = new CallExpression(
                Callee: new MemberExpression(Object: fieldRef, PropertyName: "$destroy",
                    Location: _synthLoc) { ResolvedType = blankType },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = blankType };
            statements.Add(item: new ExpressionStatement(Expression: destroyCall,
                Location: _synthLoc));
        }

        statements.Add(item: noop);
        return new BlockStatement(Statements: statements, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the variant <c>$destroy()</c>:
    /// <c>when me { is None => ; is Blank => ; is T as v => v.$destroy(); ... }</c>.
    /// Only the active arm's payload is torn down. The absent arm is matched with <c>is None</c>
    /// (variants use <c>None</c> for their empty branch); void (<c>Blank</c>) and value arms are
    /// no-ops (a value arm's <c>$destroy</c> is itself a no-op, kept for uniformity).
    /// </summary>
    private WhenStatement BuildVariantDestroyBody(VariantTypeInfo variant)
    {
        TypeInfo? blankType = ctx.Registry.LookupType(name: "Blank");
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = variant };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone ? "None" : member.Type!.Name;
            bool isVoidPayload = member is { IsNone: false, Type.Name: "Blank" };
            var typeExpr = new TypeExpression(Name: memberName, GenericArguments: null,
                Location: _synthLoc) { ResolvedType = member.Type };

            Pattern pattern;
            Statement clauseBody;
            if (member.IsNone || isVoidPayload)
            {
                // `is None` / `is Blank` — no payload to tear down.
                pattern = new TypePattern(Type: typeExpr, VariableName: null, Bindings: null,
                    Location: _synthLoc);
                clauseBody = new ReturnStatement(Value: null, Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(Type: typeExpr, VariableName: "v", Bindings: null,
                    Location: _synthLoc);
                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                    { ResolvedType = member.Type };
                var destroyCall = new CallExpression(
                    Callee: new MemberExpression(Object: vRef, PropertyName: "$destroy",
                        Location: _synthLoc) { ResolvedType = blankType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = blankType };
                clauseBody = new ExpressionStatement(Expression: destroyCall, Location: _synthLoc);
            }

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: clauseBody,
                Location: _synthLoc));
        }

        clauses.Add(item: new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(Value: null, Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    private static ReturnStatement MakeLiteralReturn(string value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static ReturnStatement MakeLiteralReturn(ulong value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: TokenType.U64Literal,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static ReturnStatement MakeLiteralReturn(long value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: TokenType.S64Literal,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static ReturnStatement MakeLiteralReturn(bool value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: value ? TokenType.True : TokenType.False,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    /// <summary>
    /// Approximates the in-memory byte size of a type for <c>data_size()</c>.
    /// Primitive types use their natural width; structs sum field sizes (8-byte aligned).
    /// Entity and crashable types return 8 (pointer size on 64-bit -> stored by reference).
    /// Backend-annotated records use the LLVM type width parsed from the @llvm("...") string.
    /// </summary>
    private static ulong CalculateDataSizeForType(TypeInfo type) =>
        type switch
        {
            TupleTypeInfo t => (ulong)(t.ElementTypes.Count * 8),
            RecordTypeInfo { HasDirectBackendType: true } r => LlvmBackendTypeSize(r.BackendType!),
            RecordTypeInfo r => (ulong)(r.MemberVariables.Count * 8),
            EntityTypeInfo => 8,   // heap-allocated; stored as pointer (8 bytes on 64-bit)
            CrashableTypeInfo => 8, // same -> stored as pointer
            VariantTypeInfo v => (ulong)((v.Members.Count + 1) * 8), // tag + largest payload
            _ => 0
        };

    /// <summary>
    /// Returns the byte size of a scalar LLVM type name as used in @llvm("...") annotations.
    /// Array types like "[4 x i32]" are parsed recursively.
    /// Template strings containing '{' (unresolved generics) return 0.
    /// </summary>
    private static ulong LlvmBackendTypeSize(string llvmType) => llvmType.Trim() switch
    {
        "void" => 0,                // Blank -> zero-sized
        "i1" or "i8" => 1,
        "i16" or "half" => 2,
        "i32" or "float" => 4,
        "i64" or "double" or "ptr" => 8,
        "i128" or "fp128" => 16,
        var s when s.StartsWith('[') => ParseLlvmArraySize(s),
        var s when s.Contains('{') => 0,  // unresolved generic template
        _ => throw new InvalidOperationException(
            $"Unknown LLVM type '{llvmType}' in LlvmBackendTypeSize — cannot determine byte size.")
    };

    /// <summary>
    /// Parses an LLVM array type like "[4 x i32]" ??4 * 4 = 16.
    /// Returns 0 if the format is unrecognised.
    /// </summary>
    private static ulong ParseLlvmArraySize(string arrayType)
    {
        // Expected format: "[ N x elemType ]"
        int xIdx = arrayType.IndexOf(" x ", StringComparison.Ordinal);
        if (xIdx < 0) return 0;

        string countPart = arrayType[1..xIdx].Trim();
        string elemPart = arrayType[(xIdx + 3)..].TrimEnd(']').Trim();

        if (!ulong.TryParse(countPart, out ulong count)) return 0;
        return count * LlvmBackendTypeSize(elemPart);
    }

    //  $represent / $diagnose (tuple)

    private void HandleTuple(RoutineInfo routine, TupleTypeInfo tuple, TypeInfo textType,
        TypeInfo? s32Type)
    {
        switch (routine.Name)
        {
            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTupleTextBody(tuple: tuple, textType: textType, diagnose: false);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTupleTextBody(tuple: tuple, textType: textType, diagnose: true);
                break;

            case "$eq":
            {
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
                if (boolType == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBody(ownerType: tuple, fields: tuple.MemberVariables, boolType: boolType);
                break;
            }

            case "$cmp":
            {
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
                if (s32Type == null || boolType == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCmpBody(ownerType: tuple, fields: tuple.MemberVariables,
                        s32Type: s32Type, boolType: boolType);
                break;
            }

            case HashMethodName:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildHashBody(ownerType: tuple, fields: tuple.MemberVariables, u64Type: u64Type);
                break;
            }
        }
    }

    /// <summary>
    /// Builds the body for <c>$represent</c> or <c>$diagnose</c> on a tuple.
    /// <list type="bullet">
    ///   <item><c>$represent</c>: <c>return f"({me.item0}, {me.item1})"</c></item>
    ///   <item><c>$diagnose</c>: <c>return f"ValueTuple[T1, T2]({me.item0}, {me.item1})"</c></item>
    /// </list>
    /// </summary>
    private static ReturnStatement BuildTupleTextBody(TupleTypeInfo tuple, TypeInfo textType,
        bool diagnose)
    {
        var parts = new List<InsertedTextPart>();

        if (diagnose)
        {
            parts.Add(new TextPart(
                Text: $"{tuple.QualifiedTypeName}(",
                Location: _synthLoc));
        }
        else
        {
            parts.Add(new TextPart(Text: "(", Location: _synthLoc));
        }

        bool first = true;
        foreach (MemberVariableInfo field in tuple.MemberVariables)
        {
            if (!first)
                parts.Add(new TextPart(Text: ", ", Location: _synthLoc));
            first = false;

            var fieldExpr = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = tuple },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            parts.Add(new ExpressionPart(
                Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring = new InsertedTextExpression(
            Parts: parts,
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    //  $represent / $diagnose (variant)

    private void HandleVariant(RoutineInfo routine, VariantTypeInfo variant, TypeInfo textType)
    {
        // Skip generic definitions -> no concrete member types to dispatch on.
        if (variant.IsGenericDefinition) return;

        switch (routine.Name)
        {
            case RepresentMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildVariantRepresentBody(variant: variant, textType: textType);
                break;

            case DiagnoseMethodName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildVariantDiagnoseBody(variant: variant, textType: textType);
                break;
        }
    }

    /// <summary>
    /// Builds: <c>when me { is Blank => return "Blank", is T as v => return v.$represent(), ... }</c>.
    /// </summary>
    private static WhenStatement BuildVariantRepresentBody(VariantTypeInfo variant, TypeInfo textType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = variant };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone ? "None" : member.Type!.Name;
            // IsNone = the absent arm (rendered as "none"). Zero-sized types like Blank or
            // an empty record are real values — render via the type's own $represent (or the
            // type name when we can't bind a void payload).
            bool isAbsentArm = member.IsNone;
            bool isVoidPayload = !isAbsentArm && member.Type?.Name == "Blank";

            var typeExpr = new TypeExpression(
                Name: memberName,
                GenericArguments: null,
                Location: _synthLoc) { ResolvedType = member.Type };

            Pattern pattern;
            Statement clauseBody;

            if (isAbsentArm)
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: null, Bindings: null, Location: _synthLoc);
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: $"{variant.ShortTypeName}(none)",
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else if (isVoidPayload)
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: null, Bindings: null, Location: _synthLoc);
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: $"{variant.ShortTypeName}({memberName})",
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: "v", Bindings: null, Location: _synthLoc);

                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                    { ResolvedType = member.Type };
                var representCall = new CallExpression(
                    Callee: new MemberExpression(
                        Object: vRef,
                        PropertyName: RepresentMethodName,
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };

                var parts = new List<InsertedTextPart>
                {
                    new TextPart(Text: $"{variant.ShortTypeName}(", Location: _synthLoc),
                    new ExpressionPart(
                        Expression: representCall,
                        FormatSpec: null,
                        Location: _synthLoc),
                    new TextPart(Text: ")", Location: _synthLoc)
                };
                var fstring = new InsertedTextExpression(
                    Parts: parts,
                    IsRaw: false,
                    Location: _synthLoc) { ResolvedType = textType };
                clauseBody = new ReturnStatement(Value: fstring, Location: _synthLoc);
            }

            clauses.Add(new WhenClause(
                Pattern: pattern,
                Body: clauseBody,
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(
                Value: new LiteralExpression(
                    Value: $"{variant.ShortTypeName}(<error>)",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Builds:
    /// <c>when me { is None => return "Mod.V(type_id: 0, none)", is T as v => return f"Mod.V(type_id: N, {v.$diagnose()})", ... }</c>.
    /// </summary>
    private static WhenStatement BuildVariantDiagnoseBody(VariantTypeInfo variant, TypeInfo textType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = variant };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone ? "None" : member.Type!.Name;
            bool isAbsentArm = member.IsNone;
            bool isVoidPayload = !isAbsentArm && member.Type?.Name == "Blank";
            ulong typeId = isAbsentArm ? 0UL : TypeIdHelper.ComputeTypeId(fullName: member.Type!.FullName);

            var typeExpr = new TypeExpression(
                Name: memberName,
                GenericArguments: null,
                Location: _synthLoc) { ResolvedType = member.Type };

            Pattern pattern;
            Statement clauseBody;

            if (isAbsentArm)
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: null, Bindings: null, Location: _synthLoc);
                string literal = $"{variant.QualifiedTypeName}(type_id: 0, none)";
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: literal,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else if (isVoidPayload)
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: null, Bindings: null, Location: _synthLoc);
                string literal = $"{variant.QualifiedTypeName}(type_id: {typeId}, {memberName})";
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: literal,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: "v", Bindings: null, Location: _synthLoc);

                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                    { ResolvedType = member.Type };
                var diagnoseCall = new CallExpression(
                    Callee: new MemberExpression(
                        Object: vRef,
                        PropertyName: DiagnoseMethodName,
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };

                string prefix = $"{variant.QualifiedTypeName}(type_id: {typeId}, ";
                var parts = new List<InsertedTextPart>
                {
                    new TextPart(Text: prefix, Location: _synthLoc),
                    new ExpressionPart(
                        Expression: diagnoseCall,
                        FormatSpec: null,
                        Location: _synthLoc),
                    new TextPart(Text: ")", Location: _synthLoc)
                };
                var fstring = new InsertedTextExpression(
                    Parts: parts,
                    IsRaw: false,
                    Location: _synthLoc) { ResolvedType = textType };
                clauseBody = new ReturnStatement(Value: fstring, Location: _synthLoc);
            }

            clauses.Add(new WhenClause(
                Pattern: pattern,
                Body: clauseBody,
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(
                Value: new LiteralExpression(
                    Value: $"{variant.QualifiedTypeName}(type_id: <error>)",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

}
