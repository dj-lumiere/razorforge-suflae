using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 2: Wired routine synthesis and derived operator generation.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Known wired methods that are valid operator/special methods. Derived from the single source
    /// of truth <see cref="Compiler.Resolution.WiredRoutineCatalog"/> (entries flagged
    /// <see cref="Compiler.Resolution.WiredView.KnownWired"/>).
    /// </summary>
    private static readonly HashSet<string> KnownWiredMethods =
        Compiler.Resolution.WiredRoutineCatalog.BuildKnownWiredMethods();

    /// <summary>
    /// Checks whether a routine declared with the wired '$' sigil names something that is NOT a known
    /// built-in wired method. The canonical name is bare ('$' is a structured attribute, not part of
    /// the name), so wired-ness is passed via <paramref name="isWired"/> rather than sniffed from the
    /// string. A non-wired routine can be named anything and is never flagged.
    /// </summary>
    private static bool IsUnknownWiredMethod(string bareName, bool isWired)
    {
        if (!isWired || bareName.Length == 0)
        {
            return false;
        }

        return !KnownWiredMethods.Contains(value: bareName);
    }

    /// <summary>
    /// Maps operator wired methods to their required protocols. Types must follow the protocol to
    /// define the operator method. Derived from the single source of truth
    /// <see cref="Compiler.Resolution.WiredRoutineCatalog"/> (entries flagged
    /// <see cref="Compiler.Resolution.WiredView.ProtocolDecl"/>).
    /// </summary>
    private static readonly Dictionary<string, List<string>> WiredToProtocols =
        Compiler.Resolution.WiredRoutineCatalog.BuildWiredToProtocols();

    /// <summary>
    /// Gets the required protocol for a wired method, or null if no protocol is required.
    /// </summary>
    internal static List<string>? GetRequiredProtocols(string wiredName)
    {
        return WiredToProtocols.GetValueOrDefault(key: wiredName);
    }

    private void CollectExternalDeclaration(ExternalDeclaration external)
    {
        // #123: Suflae cannot use C interop directly
        if (_registry.Language == Language.Suflae)
        {
            ReportError(code: SemanticDiagnosticCode.SuflaeNoCInterop,
                message:
                $"Suflae does not support C interop. External declaration '{external.Name}' is not allowed. " +
                "Use RazorForge for native interop.",
                location: external.Location);
        }

        var routineInfo = new RoutineInfo(name: external.Name)
        {
            Kind = RoutineKind.External,
            IsFailable = external.IsFailable,
            CallingConvention = external.CallingConvention,
            IsVariadic = external.IsVariadic,
            Visibility = VisibilityModifier.Open, // External declarations are always open
            Location = external.Location,
            Module = GetCurrentModuleName(),
            Annotations = external.Annotations ?? [],
            IsDangerous = external.IsDangerous,
            GenericParameters = external.GenericParameters,
            GenericConstraints = external.GenericConstraints
        };

        _registry.RegisterRoutine(routine: routineInfo);
    }

    private void TryRegisterType(TypeSymbol type, SourceLocation location)
    {
        try
        {
            _registry.RegisterType(type: type);
        }
        catch (InvalidOperationException)
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateTypeDefinition,
                message: $"Type '{type.Name}' is already defined.",
                location: location);
        }
    }

    #region Phase 2.6: Derived Operator Generation

    /// <summary>
    /// Generates derived comparison operators from $eq and $cmp routines.
    /// </summary>
    private void GenerateDerivedOperators()
    {
        new DerivedOperatorPass(_registry, _synthesizedBodies, _errors).Run();
    }

    #endregion
}
