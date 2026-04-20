namespace Compiler.Instantiation;

using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

/// <summary>
/// A pre-computed monomorphization result produced at synthesis time (Phase 6).
/// Contains everything the code generator needs to emit a concrete generic method body
/// without re-doing any AST search or type-substitution building.
/// </summary>
public sealed record MonomorphizedBody(
    /// <summary>The rewritten AST (all type-parameter names replaced with concrete type names).</summary>
    RoutineDeclaration Ast,

    /// <summary>The concrete RoutineInfo (owner/params/return fully resolved).</summary>
    RoutineInfo Info,

    /// <summary>
    /// Type substitution map (e.g., "T" → S64 TypeInfo).
    /// Used as fallback during codegen when an expression's ResolvedType still carries
    /// a generic parameter name (SA annotated the generic template, not the rewritten copy).
    /// </summary>
    Dictionary<string, TypeInfo> TypeSubs,

    /// <summary>
    /// Carrier unwrapping status for generated variants (check_/lookup_).
    /// <see cref="AsyncStatus.LookupVariant"/> → Lookup[T] carrier (ReturnType = inner T);
    /// <see cref="AsyncStatus.CheckVariant"/> → Result carrier (ReturnType = inner T).
    /// Try variants no longer set this — ReturnType IS the full Maybe[T] type.
    /// Null for regular (non-variant) methods and try_ variants.
    /// </summary>
    AsyncStatus? VariantStatus,

    /// <summary>
    /// The inner type T for carrier variants (e.g., S64 for Maybe[S64]).
    /// Null for non-variant methods.
    /// </summary>
    TypeInfo? VariantInnerType,

    /// <summary>
    /// True when the method has no source AST body (wired routines like $hash).
    /// Codegen emits the body directly via EmitSynthesizedRoutineBody.
    /// False when the Ast field contains the body to emit via GenerateFunctionDefinition.
    /// </summary>
    bool IsSynthesized);
