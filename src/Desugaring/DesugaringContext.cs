using System;
using System.Collections.Generic;
using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Resolution;
using Compiler.Synthesis;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Desugaring;

/// <summary>
/// Pre-computed data for one runtime dispatch stub, registered at Phase 6b.
/// Codegen reads KnownImplementers and ReturnTypeLlvm directly — no TypeRegistry access needed.
/// </summary>
/// <param name="Protocol">The protocol.</param>
/// <param name="MethodName">The method name.</param>
/// <param name="KnownImplementers">The known implementers.</param>
public sealed record CrashableDispatchEntry(
    ProtocolTypeInfo Protocol,
    string MethodName,
    List<TypeInfo> KnownImplementers);

/// <summary>
/// Shared context for all desugaring passes.
/// </summary>
public sealed class DesugaringContext
{
    /// <summary>The type registry from semantic analysis.</summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// Routine bodies collected during Phase 5 body analysis, keyed by RoutineInfo.RegistryKey.
    /// Used by <see cref="ErrorHandlingVariantPass"/> to generate try_/check_/lookup_ variants.
    /// </summary>
    public IReadOnlyDictionary<string, Statement> RoutineBodies { get; }

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines, keyed by the variant
    /// RoutineInfo.RegistryKey. Written by <see cref="ErrorHandlingVariantPass"/>
    /// and consumed by codegen so that variant functions emit carrier construction without
    /// relying on mutable flag fields.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; } = new();

    /// <summary>
    /// Concrete generic bodies produced by <see cref="GenericMonomorphizationPass"/>,
    /// keyed by the concrete <see cref="TypeModel.Symbols.RoutineInfo.RegistryKey"/>.
    /// Codegen checks this map before doing its own AST search and rewriting, so most
    /// generic method bodies are ready before the first IR line is emitted.
    /// </summary>
    public Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; } = new();

    /// <summary>Target platform — drives BuilderService platform constants.</summary>
    public TargetConfig Target { get; }

    /// <summary>Build mode — drives BuilderService.build_mode.</summary>
    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Runtime dispatch stubs pre-registered by <c>CrashableDispatchRegistrationPass</c>
    /// (Phase 6b). Key: <c>"{protocol.FullName}.{methodName}"</c> (raw, not LLVM-quoted).
    /// Codegen reads this instead of discovering dispatch stubs lazily during emit.
    /// </summary>
    public Dictionary<string, CrashableDispatchEntry> PendingCrashableDispatches { get; } = new();

    /// <summary>When true, diagnostic passes print per-iteration timings to stderr.</summary>
    public bool SaTiming { get; set; }

    /// <summary>
    /// Strategy-B live routine set (RegistryKey values reachable from program entry points).
    /// When non-empty, GMP gates body emission on membership; empty disables filtering.
    /// </summary>
    public HashSet<string> LiveRoutineKeys { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Live concrete owner-type FullNames mirrored from
    /// <c>InstantiationContext.LiveOwnerTypeNames</c>. GMP skips
    /// <c>ProcessConcreteType</c> for any concrete type not in this set when non-empty.
    /// </summary>
    public HashSet<string> LiveOwnerTypeNames { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Initializes shared state for passes that rewrite verified syntax before instantiation.
    /// </summary>
    public DesugaringContext(TypeRegistry registry,
        IReadOnlyDictionary<string, Statement> routineBodies,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        Registry = registry;
        RoutineBodies = routineBodies;
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
    }
}
