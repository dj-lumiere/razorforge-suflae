using System.Collections.Generic;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation;

/// <summary>
/// A pre-computed monomorphization result produced at synthesis time (Phase 6).
/// Contains everything the code generator needs to emit a concrete generic method body
/// without re-doing any AST search or type-substitution building.
/// </summary>
/// <param name="Ast">The AST.</param>
/// <param name="Info">The info.</param>
/// <param name="TypeSubs">The type substitutions.</param>
/// <param name="VariantStatus">The variant status.</param>
/// <param name="VariantInnerType">The variant inner type.</param>
/// <param name="IsSynthesized">Whether this is synthesized.</param>
public sealed record MonomorphizedBody(
    RoutineDeclaration Ast,

    RoutineInfo Info,

    Dictionary<string, TypeInfo> TypeSubs,

    AsyncStatus? VariantStatus,

    TypeInfo? VariantInnerType,

    bool IsSynthesized);
