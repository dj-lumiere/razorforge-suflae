using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Postprocessing;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Reprs;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Type mapping: RazorForge/Suflae types -> LLVM IR types.
/// </summary>
public partial class LlvmCodeGenerator
{
    #region Type Mapping

    /// <summary>
    /// Gets the LLVM type name for a TypeInfo.
    /// </summary>
    /// <param name="type">The type to convert.</param>
    /// <returns>The LLVM type string.</returns>
    private TypeInfo ResolveTypeSubstitution(TypeInfo type) // NOSONAR S3776
    {
        if (_typeSubstitutions == null)
        {
            return type;
        }

        // Direct generic parameter substitution (e.g., K -> S64)
        if (_typeSubstitutions.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Wrapper-forwarder rename fallback: the disambiguated param carries the original
        // inner-T name in `ForwarderOriginalName`. The substitution map is keyed by that
        // original name (see BuildResolvedRoutineTypeSubstitutions + WrapperForwardingPass).
        if (type is GenericParameterTypeInfo { ForwarderOriginalName: { } originalInnerName }
            && _typeSubstitutions.TryGetValue(key: originalInnerName, value: out TypeInfo? renamed))
        {
            return renamed;
        }

        // Parameterized types with unresolved args (e.g., DictEntry[K, V] -> DictEntry[S64, Text])
        if (type.TypeArguments is { Count: > 0 })
        {
            bool anyResolved = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (TypeInfo ta in type.TypeArguments)
            {
                TypeInfo resolved = ResolveTypeSubstitution(type: ta);
                resolvedArgs.Add(item: resolved);
                if (resolved != ta)
                {
                    anyResolved = true;
                }
            }

            if (anyResolved)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: resolvedArgs);
                }

                // WrapperTypeInfo (Retained[T], etc.) has no GenericDefinition ->
                // look up the RecordTypeInfo definition by wrapper name.
                if (type is WrapperTypeInfo)
                {
                    TypeInfo? wrapperRecordDef = _registry.LookupType(name: type.Name);
                    if (wrapperRecordDef is { IsGenericDefinition: true })
                        return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                            typeArguments: resolvedArgs);
                }
            }
        }

        return type;
    }

    /// <summary>
    /// Gets the LLVM type needed by this compiler phase.
    /// </summary>
    private static string GetLlvmType(BackendRepr repr)
    {
        return repr.LlvmAbiType;
    }

    /// <summary>
    /// Gets the expression LLVM type needed by this compiler phase.
    /// </summary>
    private string GetExpressionLlvmType(Expression expr, string fallback = "i64")
    {
        if (expr.ResolvedRepr != null)
        {
            return GetLlvmType(repr: expr.ResolvedRepr);
        }

        TypeInfo? type = GetExpressionType(expr: expr);
        return type != null
            ? GetLlvmType(type: type)
            : fallback;
    }

    /// <summary>
    /// Gets the LLVM type needed by this compiler phase.
    /// </summary>
    /// <summary>
    /// The in-MEMORY/aggregate LLVM type of a record field. <c>Bool</c> (register type <c>i1</c>) is
    /// stored as <c>i8</c> inside structs: an <c>i1</c> field in a value record returned by <c>sret</c>
    /// miscompiles at -O3 (its 1-bit-value / 1-byte-slot duality confuses SROA/store-forwarding, which
    /// corrupts the neighbouring wide field). Using the honest byte type and zext/trunc at the field
    /// boundary — exactly how clang lowers C++ <c>bool</c> — removes the <c>i1</c> from the aggregate.
    /// The struct size is unchanged (an <c>i1</c> already occupied a byte), so field offsets are stable.
    /// </summary>
    private string GetFieldStorageLlvmType(TypeInfo type) =>
        FieldNeedsBoolStorage(type: type) ? "i8" : GetValueLlvmType(type: type);

    /// <summary>True when a record field's register type is <c>i1</c> (Bool) and needs <c>i8</c> storage.</summary>
    private bool FieldNeedsBoolStorage(TypeInfo type) => GetLlvmType(type: type) is "i1";

    /// <summary>
    /// The LLVM type for STORING a value of <paramref name="type"/> (an alloca, a struct field, a
    /// by-value parameter). Identical to <see cref="GetLlvmType"/> except for <c>Blank</c>: Blank is
    /// <c>@llvm("void")</c>, and <c>void</c> is illegal as a value (you cannot <c>alloca void</c> or
    /// put a <c>void</c> field in a struct). A stored Blank is the empty record <c>{}</c> — a real
    /// zero-size value. Direct routine RETURNS keep using <see cref="GetLlvmType"/> (so void-returning
    /// routines stay <c>void</c>); only Blank-as-a-VALUE uses this.
    /// </summary>
    private string GetValueLlvmType(TypeInfo type)
    {
        string t = GetLlvmType(type: type);
        return t == "void" ? "{}" : t;
    }

    /// <summary>zext an <c>i1</c> Bool value to its <c>i8</c> storage form before writing an aggregate field.</summary>
    private string CoerceBoolToStorage(System.Text.StringBuilder sb, string value, TypeInfo fieldType)
    {
        if (!FieldNeedsBoolStorage(type: fieldType)) return value;
        string t = NextTemp();
        EmitLine(sb: sb, line: $"  {t} = zext i1 {value} to i8");
        return t;
    }

    /// <summary>trunc an <c>i8</c> storage Bool back to <c>i1</c> after reading an aggregate field.</summary>
    private string CoerceStorageToBool(System.Text.StringBuilder sb, string storageValue, TypeInfo fieldType)
    {
        if (!FieldNeedsBoolStorage(type: fieldType)) return storageValue;
        string t = NextTemp();
        EmitLine(sb: sb, line: $"  {t} = trunc i8 {storageValue} to i1");
        return t;
    }

    private string GetLlvmType(TypeInfo type)
    {
        type = ResolveTypeSubstitution(type: type);
        return type switch
        {
            // Records with @llvm annotation -> use backend type directly (skip generic definitions with template holes)
            RecordTypeInfo
            {
                HasDirectBackendType: true, IsGenericDefinition: false
            } record => record.LlvmType,

            // Generic definition records (unresolved) -> pointer fallback
            RecordTypeInfo { IsGenericDefinition: true } => "ptr",

            // Records with no fields -> look up the registered definition (may have @llvm annotation)
            RecordTypeInfo { MemberVariables.Count: 0 } record when _registry.LookupType(
                name: record.Name) is RecordTypeInfo
            {
                HasDirectBackendType: true
            } llvmRecord => llvmRecord.LlvmType,

            // Variants -> struct { tag, payload }. Variant is a RecordTypeInfo subclass, so this
            // MUST precede the RecordTypeInfo arms below or a variant would be treated as a record.
            VariantTypeInfo variant => GetVariantTypeName(variant: variant),

            // Records with no fields and generic base type has @llvm annotation
            RecordTypeInfo
            {
                MemberVariables.Count: 0,
                GenericDefinition: { HasDirectBackendType: true } baseRecord
            } => baseRecord.LlvmType,

            // Multi-member-variable records -> LLVM struct type.
            // Also ensure the struct declaration is emitted -> carrier types like Result[Result[T]]
            // may be created on-demand without being registered, so the type loop never sees them.
            RecordTypeInfo record => EnsureRecordTypeDeclared(record: record),

            // Entities (and Crashable, an entity subclass) -> pointer to LLVM struct
            EntityTypeInfo => "ptr",

            // Wrappers (Viewing, Modifying, Hijacked, etc.) -> all pointers at LLVM level
            WrapperTypeInfo => "ptr",

            // Protocols -> type-erased pointer (protocol-typed fields/params hold a handle to a concrete object)
            ProtocolTypeInfo => "ptr",

            // Routine types (function pointers) -> opaque pointer
            RoutineTypeInfo => "ptr",

            // Const generic values -> map to the underlying integer type
            ConstGenericValueTypeInfo => "i64",

            // Unresolved generic parameter -> illegal in codegen. All type parameters must be
            // substituted by GenericMonomorphizationPass before the backend is entered.
            GenericParameterTypeInfo gp => throw new InvalidOperationException(
                $"GenericParameterTypeInfo '{gp.Name}' reached GetLlvmType ??" +
                "all generic parameters must be substituted before codegen entry. " +
                "Check that GenericMonomorphizationPass ran and GenericAstRewriter " +
                "annotated all expression ResolvedTypes."),

            // Error placeholder
            ErrorTypeInfo => throw new InvalidOperationException(
                message:
                "Error type found in codegen - semantic analysis should have caught this"),

            // Unknown
            _ => throw new InvalidOperationException(
                message: $"Unknown type category: {type.Category}")
        };
    }

    /// <summary>
    /// Gets the LLVM struct type name for a record, ensuring its declaration is emitted.
    /// Called from GetLLVMType so on-demand records (e.g., Result[Result[T]]) are always declared.
    /// </summary>
    private string EnsureRecordTypeDeclared(RecordTypeInfo record)
    {
        string name = GetRecordTypeName(record: record);
        // Proactively declare if not yet emitted -> covers types created on-demand
        // that are never visited by the registry iteration in GenerateTypes().
        if (!_generatedTypes.Contains(item: name))
            GenerateRecordType(record: record);
        return name;
    }

    /// <summary>
    /// Gets the LLVM struct type name for a record.
    /// </summary>
    private static string GetRecordTypeName(RecordTypeInfo record)
    {
        // Module-qualified (TypeInfo.FullName) so same-named records in different modules never
        // collide into one LLVM struct name (which LLVM would silently rename to `.0`).
        return $"%{Q(name: $"Record.{record.FullName}")}";
    }

    /// <summary>The LLVM struct name for an entity — no generation side effect. Used INSIDE
    /// GenerateEntityType (where ensuring would re-enter) and other name-only contexts. Uses the
    /// module-qualified <see cref="TypeInfo.FullName"/> (e.g. <c>Entity.Random.Random</c>,
    /// <c>Entity.Core.List[Core.S64]</c>) so same-named entities in different modules never collide
    /// into one LLVM struct name (which LLVM would silently rename to <c>.0</c> and miscompile).</summary>
    private static string RawEntityTypeName(EntityTypeInfo entity)
        => $"%{Q(name: $"Entity.{entity.FullName}")}";

    /// <summary>The bare LLVM struct name for a crashable — no generation side effect.</summary>
    private static string RawCrashableTypeName(CrashableTypeInfo crashable)
        => $"%{Q(name: $"Crashable.{crashable.FullName}")}";

    /// <summary>
    /// Gets the LLVM struct type name for an entity, ensuring its struct definition is emitted on
    /// first use. Entity structs are referenced only at use sites (alloc / field-access / size GEP),
    /// never as a by-value field (entity fields are `ptr`), so on-demand emission here lets the broad
    /// registry sweep be skipped — pruning entities the program never touches.
    /// </summary>
    private string GetEntityTypeName(EntityTypeInfo entity)
    {
        string name = RawEntityTypeName(entity: entity);
        if (!_generatedTypes.Contains(item: name)
            && !entity.IsGenericDefinition
            && !(entity.TypeArguments is { Count: > 0 } a && a.Any(predicate: ContainsGenericParameter)))
        {
            GenerateEntityType(entity: entity);
        }
        return name;
    }

    /// <summary>
    /// Gets the LLVM struct type name for a crashable type, ensuring its struct definition is emitted
    /// on first use (crashables are referenced opaquely in size GEPs and field access).
    /// </summary>
    private string GetCrashableTypeName(CrashableTypeInfo crashable)
    {
        string name = RawCrashableTypeName(crashable: crashable);
        if (!_generatedTypes.Contains(item: name)
            && !crashable.IsGenericDefinition
            && !(crashable.TypeArguments is { Count: > 0 } a && a.Any(predicate: ContainsGenericParameter)))
        {
            GenerateCrashableType(crashable: crashable);
        }
        return name;
    }

    /// <summary>The bare LLVM struct name for a variant — no generation side effect.</summary>
    private static string RawVariantTypeName(VariantTypeInfo variant)
        => $"%{Q(name: $"Variant.{variant.FullName}")}";

    /// <summary>
    /// Gets the LLVM struct type name for a variant, ensuring its struct (tag + payload) is emitted
    /// on first use — variants are passed/returned by value, so the def must exist.
    /// </summary>
    private string GetVariantTypeName(VariantTypeInfo variant)
    {
        string name = RawVariantTypeName(variant: variant);
        if (!_generatedTypes.Contains(item: name)
            && !variant.IsGenericDefinition
            && !(variant.TypeArguments is { Count: > 0 } a && a.Any(predicate: ContainsGenericParameter)))
        {
            GenerateVariantType(variant: variant);
        }
        return name;
    }

    /// <summary>
    /// Returns the named LLVM type for an error-handling carrier (Maybe[T], Result[T], Lookup[T]).
    /// Delegates to GetLLVMType -> carrier layouts come from their Standard library definitions.
    /// </summary>
    private string GetCarrierLlvmType(TypeInfo type) => GetLlvmType(type: type);

    /// <summary>
    /// Returns the named LLVM type for a Maybe[T] carrier given the inner value type T.
    /// Looks up the resolved Maybe[T] in the registry; falls back to constructing the name directly.
    /// </summary>
    private string GetMaybeCarrierLlvmType(TypeInfo valueType)
    {
        TypeInfo? def = _registry.LookupType(name: "Maybe");
        if (def != null)
        {
            TypeInfo? resolved = _registry.TryGetResolution(genericDef: def, typeArguments: [valueType]);
            if (resolved != null)
                return GetLlvmType(type: resolved);
        }
        // Carriers live in `module Core`; match the module-qualified canonical name (GetRecordTypeName).
        return $"%{Q(name: $"Record.Core.Maybe[{valueType.FullName}]")}";
    }

    /// <summary>
    /// Returns the named LLVM type for a Lookup[T] carrier given the inner value type T.
    /// </summary>
    private string GetLookupCarrierLlvmType(TypeInfo valueType)
    {
        TypeInfo? def = _registry.LookupType(name: "Lookup");
        if (def != null)
        {
            TypeInfo? resolved = _registry.TryGetResolution(genericDef: def, typeArguments: [valueType]);
            if (resolved != null)
                return GetLlvmType(type: resolved);
        }
        // Carriers live in `module Core`; match the module-qualified canonical name (GetRecordTypeName).
        return $"%{Q(name: $"Record.Core.Lookup[{valueType.FullName}]")}";
    }

    /// <summary>
    /// Returns the named LLVM type for a Result[T] carrier given the inner value type T.
    /// </summary>
    private string GetResultCarrierLlvmType(TypeInfo valueType)
    {
        TypeInfo? def = _registry.LookupType(name: "Result");
        if (def != null)
        {
            TypeInfo? resolved = _registry.TryGetResolution(genericDef: def, typeArguments: [valueType]);
            if (resolved != null)
                return GetLlvmType(type: resolved);
        }
        // Carriers live in `module Core`; match the module-qualified canonical name (GetRecordTypeName).
        return $"%{Q(name: $"Record.Core.Result[{valueType.FullName}]")}";
    }

    /// <summary>Returns true if <paramref name="type"/> is a Maybe[T], Result[T], or Lookup[T] carrier.</summary>
    private static bool IsCarrierType(TypeInfo type) =>
        type is RecordTypeInfo { CarrierKind: not CarrierKind.None };

    /// <summary>Returns true if <paramref name="type"/> is a Maybe[T] carrier.</summary>
    private static bool IsMaybeType(TypeInfo type) =>
        type is RecordTypeInfo { CarrierKind: CarrierKind.Maybe };

    /// <summary>
    /// Quotes an LLVM identifier if it contains characters that require quoting.
    /// LLVM allows any characters in quoted identifiers: @"Hijacked[Point].$eq", %"Record.Hijacked[Point]".
    /// Unquoted identifiers only allow [a-zA-Z$._0-9-].
    /// </summary>
    private static string Q(string name)
    {
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c: c) && c != '$' && c != '.' && c != '_' && c != '-')
            {
                return $"\"{name}\"";
            }
        }

        return name;
    }

    /// <summary>
    /// Gets the LLVM type for a function parameter or return type.
    /// For entities, this returns ptr (all entities are pointers).
    /// For records, this returns the struct type (passed by value).
    /// </summary>
    private string GetParameterLlvmType(TypeInfo type)
    {
        type = ResolveTypeSubstitution(type: type);
        return type switch
        {
            // Entities (and Crashable, an entity subclass) are always passed as pointers
            EntityTypeInfo => "ptr",

            // Other types use normal mapping
            _ => GetLlvmType(type: type)
        };
    }

    /// <summary>
    /// Gets the size in bytes for a type. Delegates to <see cref="TypeInfo.SizeBytes"/>
    /// so each type kind owns its own size rule.
    /// </summary>
    private int GetTypeSize(TypeInfo type) => type.SizeBytes(pointerSize: _pointerSizeBytes);

    /// <summary>
    /// Aligns a size to a given alignment.
    /// </summary>
    private static int AlignTo(int size, int alignment)
    {
        return (size + alignment - 1) / alignment * alignment;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is an unsigned integer type.
    /// Uses protocol conformance: unsigned types obey <c>UnsignedIntegral</c>.
    /// </summary>
    private static bool IsUnsignedIntegerType(TypeInfo? type)
    {
        return type is RecordTypeInfo record &&
               record.ImplementedProtocols.Any(p => p.Name == "UnsignedIntegral");
    }

    #endregion

    // Node layout (appended once at module end):
    //  !0 = root
    //  !1 = i1 !2 = i8 !3 = i16 !4 = i32
    //  !5 = i64 !6 = float !7 = double !8 = ptr
    //  !9 = half !10 = fp128 !11 = i128
    //  !12 = i1 access tag !13 = i8 !14 = i16 !15 = i32
    //  !16 = i64 access tag !17 = float !18 = double !19 = ptr
    //  !20 = half access tag !21 = fp128 !22 = i128

    /// <summary>
    /// Stores the TBAA metadata section state used by this compiler phase.
    /// </summary>
    private static readonly string TbaaMetadataSection =
        "; TBAA metadata\n" +
        "!0 = !{!\"RF TBAA Root\"}\n" +
        "!1 = !{!\"i1\", !0}\n" +
        "!2 = !{!\"i8\", !0}\n" +
        "!3 = !{!\"i16\", !0}\n" +
        "!4 = !{!\"i32\", !0}\n" +
        "!5 = !{!\"i64\", !0}\n" +
        "!6 = !{!\"float\", !0}\n" +
        "!7 = !{!\"double\", !0}\n" +
        "!8 = !{!\"ptr\", !0}\n" +
        "!9 = !{!\"half\", !0}\n" +
        "!10 = !{!\"fp128\", !0}\n" +
        "!11 = !{!\"i128\", !0}\n" +
        "!12 = !{!1,  !1,  i64 0}\n" +
        "!13 = !{!2,  !2,  i64 0}\n" +
        "!14 = !{!3,  !3,  i64 0}\n" +
        "!15 = !{!4,  !4,  i64 0}\n" +
        "!16 = !{!5,  !5,  i64 0}\n" +
        "!17 = !{!6,  !6,  i64 0}\n" +
        "!18 = !{!7,  !7,  i64 0}\n" +
        "!19 = !{!8,  !8,  i64 0}\n" +
        "!20 = !{!9,  !9,  i64 0}\n" +
        "!21 = !{!10, !10, i64 0}\n" +
        "!22 = !{!11, !11, i64 0}\n";

    private static readonly Dictionary<string, string> TbaaTagByLlvmType = new()
    {
        ["i1"]     = ", !tbaa !12",
        ["i8"]     = ", !tbaa !13",
        ["i16"]    = ", !tbaa !14",
        ["i32"]    = ", !tbaa !15",
        ["i64"]    = ", !tbaa !16",
        ["float"]  = ", !tbaa !17",
        ["double"] = ", !tbaa !18",
        ["ptr"]    = ", !tbaa !19",
        ["half"]   = ", !tbaa !20",
        ["fp128"]  = ", !tbaa !21",
        ["i128"]   = ", !tbaa !22",
    };

    /// <summary>
    /// Performs the apply TBAA step for this compiler phase.
    /// </summary>
    private static string ApplyTbaa(string ir)
    {
        var lines = ir.Split('\n');
        var sb = new System.Text.StringBuilder(capacity: ir.Length + 2048);
        foreach (var line in lines)
            sb.Append(TagLine(line)).Append('\n');
        sb.Append(TbaaMetadataSection);
        return sb.ToString();
    }

    /// <summary>
    /// Performs the tag line step for this compiler phase.
    /// </summary>
    private static string TagLine(string line)
    {
        if (line.Contains("!tbaa")) return line;

        var t = line.AsSpan().TrimStart();

        // load: " %x = load TYPE, ptr ..."
        int loadIdx = line.IndexOf(" = load ", StringComparison.Ordinal);
        if (loadIdx >= 0)
        {
            int typeStart = loadIdx + " = load ".Length;
            int comma = line.IndexOf(',', typeStart);
            if (comma > typeStart)
            {
                var llvmType = line[typeStart..comma].Trim();
                if (TbaaTagByLlvmType.TryGetValue(llvmType, out var tag))
                    return line + tag;
            }
            return line;
        }

        // store: " store TYPE VALUE, ptr ..."
        if (t.StartsWith("store ", StringComparison.Ordinal))
        {
            int storeStart = line.IndexOf("store ", StringComparison.Ordinal) + "store ".Length;
            int space = line.IndexOf(' ', storeStart);
            if (space > storeStart)
            {
                var llvmType = line[storeStart..space].Trim();
                if (TbaaTagByLlvmType.TryGetValue(llvmType, out var tag))
                    return line + tag;
            }
        }

        return line;
    }

    // -----------------------------------------------------------------------------

    /// <summary>Bundles a method lookup result with fully-resolved context for codegen emission.</summary>
    private record ResolvedMemberRoutine(
        RoutineInfo Routine,
        TypeInfo OwnerType,
        bool IsFailable,
        List<string>? ModulePath,
        string MangledName,
        bool IsMonomorphized,
        Dictionary<string, TypeInfo>? MemberRoutineTypeArgs
    );

    /// <summary>
    /// Infers method-level type arguments from concrete argument types.
    /// Returns a mapping of generic parameter names to concrete types, or null if inference fails.
    /// Only infers parameters that belong to the method itself (excludes owner-level params).
    /// </summary>
    private static Dictionary<string, TypeInfo>? InferMemberRoutineTypeArgs(RoutineInfo genericMethod,
        List<TypeInfo> argTypes)
    {
        if (genericMethod.GenericParameters == null)
        {
            return null;
        }

        var ownerParams = new HashSet<string>();
        if (genericMethod.OwnerType?.GenericParameters != null)
        {
            foreach (string gp in genericMethod.OwnerType.GenericParameters)
            {
                ownerParams.Add(item: gp);
            }
        }

        var methodParams = genericMethod.GenericParameters
            .Where(predicate: gp => !ownerParams.Contains(item: gp))
            .ToHashSet();
        if (methodParams.Count == 0)
        {
            return null;
        }

        var inferred = new Dictionary<string, TypeInfo>();

        for (int i = 0; i < genericMethod.Parameters.Count && i < argTypes.Count; i++)
        {
            TypeInfo paramType = genericMethod.Parameters[index: i].Type;
            TypeInfo argType = argTypes[index: i];
            InferFromTypes(paramType: paramType,
                argType: argType,
                methodParams: methodParams,
                inferred: inferred);
        }

        return inferred.Count == methodParams.Count
            ? inferred
            : null;
    }

    /// <summary>
    /// Recursively infers type argument mappings by matching a generic parameter type against a concrete type.
    /// Handles direct params (T -> S64) and parameterized types (List[T] -> List[S64]).
    /// </summary>
    private static void InferFromTypes(TypeInfo paramType, TypeInfo argType,
        HashSet<string> methodParams, Dictionary<string, TypeInfo> inferred)
    {
        if (paramType is GenericParameterTypeInfo && methodParams.Contains(item: paramType.Name))
        {
            inferred.TryAdd(key: paramType.Name, value: argType);
            return;
        }

        if (paramType is { IsGenericResolution: true, TypeArguments: not null } &&
            argType is { IsGenericResolution: true, TypeArguments: not null } &&
            paramType.TypeArguments.Count == argType.TypeArguments.Count)
        {
            for (int i = 0; i < paramType.TypeArguments.Count; i++)
            {
                InferFromTypes(paramType: paramType.TypeArguments[index: i],
                    argType: argType.TypeArguments[index: i],
                    methodParams: methodParams,
                    inferred: inferred);
            }
        }
    }

    /// <summary>
    /// Looks up a method on a type and returns a fully-resolved bundle for codegen.
    /// Generic instantiation must already be complete before this runs.
    /// </summary>
    private ResolvedMemberRoutine? ResolveMemberRoutine(TypeInfo receiverType, string methodName,
        bool? isFailable = null,
        List<TypeInfo>? methodTypeArgs = null,
        List<TypeInfo>? argTypes = null)
    {
        receiverType = ApplyTypeSubstitutions(type: receiverType);
        List<TypeInfo>? resolvedArgTypes = argTypes?
            .Select(selector: ApplyTypeSubstitutions)
            .ToList();

        RoutineInfo? method = _registry.LookupMethod(type: receiverType,
            methodName: methodName,
            isFailable: isFailable);
        if (method?.IsGenericDefinition == true && resolvedArgTypes is { Count: > 0 })
        {
            method = _registry.LookupMethodOverload(type: receiverType,
                methodName: methodName,
                argTypes: resolvedArgTypes);
        }

        if (method == null)
        {
            return null;
        }

        if (methodTypeArgs is { Count: > 0 } ||
            resolvedArgTypes is { Count: > 0 } && method.IsGenericDefinition)
        {
            throw new InvalidOperationException(
                $"Method-level generic instantiation for '{receiverType.FullName}.{methodName}' reached LLVM codegen. " +
                "Instantiate it before codegen.");
        }

        if (method.IsGenericDefinition || method.OwnerType is GenericParameterTypeInfo)
        {
            // Synthesized wrapper forwarder: the raw generic-def-anchored version was returned
            // instead of the concrete instance. The concrete body will be emitted by Phase C;
            // return null so the caller falls back to a placeholder mangled name and the
            // define-vs-declare conflict is resolved at the final IR assembly step.
            if (method is { IsSynthesized: true, WrapperForwarderInnerMethod: not null })
                return null;
            throw new InvalidOperationException(
                $"Unresolved generic method '{receiverType.FullName}.{methodName}' reached LLVM codegen.");
        }

        string mangledName = MangleRoutineName(routine: method);

        return new ResolvedMemberRoutine(
            Routine: method,
            OwnerType: receiverType,
            IsFailable: method.IsFailable,
            ModulePath: method.ModulePath,
            MangledName: mangledName,
            IsMonomorphized: false,
            MemberRoutineTypeArgs: null
        );
    }

    /// <summary>
    /// Substitutes a generic parameter name with a concrete type in a type expression.
    /// Handles both direct substitution (T -> Point) and nested resolution (Viewing[T] -> Viewing[Point]).
    /// </summary>
    private TypeInfo SubstituteGenericParamInType(TypeInfo type, string paramName,
        TypeInfo concreteType) // NOSONAR S3776
    {
        if (type.Name == paramName || type is GenericParameterTypeInfo gp && gp.Name == paramName)
        {
            return concreteType;
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anyChanged = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo substituted = SubstituteGenericParamInType(type: arg,
                    paramName: paramName,
                    concreteType: concreteType);
                substitutedArgs.Add(item: substituted);
                if (!ReferenceEquals(objA: substituted, objB: arg))
                {
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: substitutedArgs);
                }

                if (type is WrapperTypeInfo)
                {
                    TypeInfo? wrapperRecordDef = _registry.LookupType(name: type.Name);
                    if (wrapperRecordDef is { IsGenericDefinition: true })
                    {
                        return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                            typeArguments: substitutedArgs);
                    }
                }
            }
        }

        return type;
    }

    /// <summary>
    /// Rebinds a semantically-resolved routine to the concrete owner/return type seen by codegen
    /// when the carried routine still points at a generic definition or partial resolution.
    /// </summary>
    private RoutineInfo? NormalizeResolvedRoutineReference(RoutineInfo? routine,
        TypeInfo? receiverType, TypeInfo? returnType, List<TypeInfo> argTypes)
    {
        if (routine == null)
        {
            return null;
        }

        receiverType = NormalizeRoutineLookupType(type: receiverType);
        returnType = NormalizeRoutineLookupType(type: returnType);
        string lookupMethodName = GetMemberRoutineLookupName(routine);

        bool ownerMismatch = receiverType != null &&
                             routine.OwnerType is { } ownerType and not ProtocolTypeInfo &&
                             NormalizeRoutineLookupType(type: ownerType)?.FullName != receiverType.FullName;

        if (receiverType != null &&
            (ownerMismatch ||
             routine.OwnerType is { IsGenericDefinition: true } ||
             routine.IsGenericDefinition ||
             RoutineHasUnresolvedTypeArguments(routine: routine)))
        {
            RoutineInfo? reboundMethod = _registry.LookupMethodOverload(type: receiverType,
                methodName: lookupMethodName,
                argTypes: argTypes);
            reboundMethod ??= _registry.LookupMethod(type: receiverType,
                methodName: lookupMethodName,
                isFailable: routine.IsFailable);
            if (reboundMethod != null)
            {
                return reboundMethod;
            }
        }

        if (returnType != null && routine.Name == "$create")
        {
            RoutineInfo? reboundCreator = _registry.LookupMethodOverload(type: returnType,
                methodName: "$create",
                argTypes: argTypes);
            reboundCreator ??= _registry.LookupRoutineOverload(
                baseName: $"{returnType.Name}.$create",
                argTypes: argTypes);
            // Only accept the rebound when its arity matches the call. The fallback path inside
            // LookupRoutineOverload returns the first-registered overload (often the zero-arg
            // $create) when nothing matches the arg types, which would otherwise clobber SA's
            // correct overload resolution and emit a call to the wrong symbol.
            if (reboundCreator != null && reboundCreator.Parameters.Count == argTypes.Count)
            {
                return reboundCreator;
            }
        }

        return routine;
    }

    /// <summary>
    /// Gets the member routine lookup name needed by this compiler phase.
    /// </summary>
    private static string GetMemberRoutineLookupName(RoutineInfo routine)
    {
        string name = !string.IsNullOrEmpty(routine.BaseName)
            ? routine.BaseName
            : routine.Name;
        int dotIndex = name.LastIndexOf('.');
        return dotIndex >= 0 && dotIndex + 1 < name.Length
            ? name[(dotIndex + 1)..]
            : name;
    }

    /// <summary>
    /// Returns true when a carried routine still contains unresolved generic or error placeholders.
    /// </summary>
    private static bool RoutineHasUnresolvedTypeArguments(RoutineInfo routine)
    {
        if (routine.TypeArguments is { Count: > 0 } routineArgs && HasUnresolvedParam(routineArgs))
        {
            return true;
        }

        TypeInfo? owner = routine.OwnerType;
        return owner?.TypeArguments is { Count: > 0 } ownerArgs && HasUnresolvedParam(ownerArgs);

        static bool HasUnresolvedParam(List<TypeInfo> types)
        {
            foreach (TypeInfo t in types)
            {
                if (t is GenericParameterTypeInfo or ErrorTypeInfo)
                {
                    return true;
                }

                if (t.TypeArguments is { Count: > 0 } inner && HasUnresolvedParam(inner))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Normalizes wrapper lookup types to their canonical registry-backed generic resolutions.
    /// </summary>
    private TypeInfo? NormalizeRoutineLookupType(TypeInfo? type)
    {
        if (type is WrapperTypeInfo wrapperType &&
            _registry.LookupType(name: wrapperType.Name) is { IsGenericDefinition: true } wrapperDef &&
            wrapperType.TypeArguments is { Count: > 0 })
        {
            return _registry.GetOrCreateResolution(genericDef: wrapperDef,
                typeArguments: wrapperType.TypeArguments);
        }

        return type;
    }
}
