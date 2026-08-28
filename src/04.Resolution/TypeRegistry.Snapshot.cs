using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Declaration;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;
using Verification.Scopes;

namespace Compiler.Resolution;

partial class TypeRegistry
{
    /// <summary>
    /// Immutable snapshot of the registry state after stdlib loading and body analysis.
    /// Used by tests to restore pre-analyzed stdlib state instead of re-parsing 168 files per test.
    /// </summary>
    public sealed class StdlibSnapshot
    {
        /// <summary>The language (RazorForge or Suflae) this snapshot was built for.</summary>
        public Language Language { get; init; }

        // Type storage
        /// <summary>All registered types keyed by full name.</summary>
        public Dictionary<string, TypeInfo> Types { get; init; } = null!;
        /// <summary>Generic-resolution cache keyed by instantiated full name.</summary>
        public Dictionary<string, TypeInfo> Resolutions { get; init; } = null!;
        /// <summary>Wrapper-type resolution cache (Owned/Retained/etc.) keyed by full name.</summary>
        public Dictionary<string, WrapperTypeInfo> WrapperResolutions { get; init; } = null!;
        /// <summary>Entity specializations keyed by full name.</summary>
        public Dictionary<string, TypeInfo> EntitySpecializations { get; init; } = null!;
        /// <summary>Types indexed by short (unqualified) name for import resolution.</summary>
        public Dictionary<string, TypeInfo> TypesByShortName { get; init; } = null!;

        // Routine storage — list-valued dicts need copied lists (not shared) so tests can extend them
        /// <summary>All routines keyed by RegistryKey.</summary>
        public Dictionary<string, RoutineInfo> Routines { get; init; } = null!;
        /// <summary>Routines keyed by qualified name.</summary>
        public Dictionary<string, RoutineInfo> RoutinesByQualifiedName { get; init; } = null!;
        /// <summary>Routines grouped by owner type full name, then by memberRoutine name → overloads (a bare
        /// generic-param owner is stored under the canonical GenericOwnerKey).</summary>
        public Dictionary<string, Dictionary<string, List<RoutineInfo>>> RoutinesByOwner { get; init; } = null!;
        /// <summary>Resolved routine instances (concrete-owner substitutions) keyed by RegistryKey.</summary>
        public Dictionary<string, RoutineInfo> RoutineResolutions { get; init; } = null!;

        // Preset storage
        /// <summary>Preset variables keyed by short name.</summary>
        public Dictionary<string, VariableInfo> Presets { get; init; } = null!;
        /// <summary>Preset variables keyed by qualified name.</summary>
        public Dictionary<string, VariableInfo> PresetsByQualifiedName { get; init; } = null!;

        // Module tracking
        /// <summary>Set of module paths already loaded into the registry.</summary>
        public HashSet<string> LoadedModules { get; init; } = null!;
        /// <summary>Module alias → qualified module name map.</summary>
        public Dictionary<string, string> ModuleNames { get; init; } = null!;

        /// <summary>Root path for on-demand module loading (e.g. import BuilderQuery).</summary>
        public string? StdlibRootPath { get; init; }
    }

    /// <summary>
    /// Captures a snapshot of the current registry state.
    /// Call after stdlib loading and body analysis is complete.
    /// List-valued dictionaries are deep-copied so tests can extend them without poisoning the snapshot.
    /// </summary>
    public StdlibSnapshot CaptureSnapshot() =>
        new()
        {
            Language = Language,
            Types = new Dictionary<string, TypeInfo>(_types),
            Resolutions = new Dictionary<string, TypeInfo>(_resolutions),
            WrapperResolutions = new Dictionary<string, WrapperTypeInfo>(_wrapperResolutions),
            EntitySpecializations = new Dictionary<string, TypeInfo>(_entitySpecializations),
            TypesByShortName = new Dictionary<string, TypeInfo>(_typesByShortName),
            Routines = new Dictionary<string, RoutineInfo>(_routines),
            RoutinesByQualifiedName = new Dictionary<string, RoutineInfo>(_routinesByQualifiedName),
            RoutinesByOwner = _routinesByOwner.ToDictionary(
                keySelector: kv => kv.Key,
                elementSelector: kv => kv.Value.ToDictionary(
                    keySelector: m => m.Key,
                    elementSelector: m => new List<RoutineInfo>(m.Value))),
            RoutineResolutions = new Dictionary<string, RoutineInfo>(_routineResolutions),
            Presets = new Dictionary<string, VariableInfo>(_presets),
            PresetsByQualifiedName = new Dictionary<string, VariableInfo>(_presetsByQualifiedName),
            LoadedModules = new HashSet<string>(_loadedModules, StringComparer.OrdinalIgnoreCase),
            ModuleNames = new Dictionary<string, string>(_moduleNames, StringComparer.OrdinalIgnoreCase),
            StdlibRootPath = _stdlibPath,
        };

    /// <summary>
    /// Restores registry state from a stdlib snapshot.
    /// Used by tests to skip the expensive stdlib parse + body analysis phases.
    /// </summary>
    public TypeRegistry(Language language, StdlibSnapshot snapshot) // NOSONAR S3776
    {
        Language = language;
        _ambient = this;
        GlobalScope = new Scope(kind: ScopeKind.Global);
        _currentScope = GlobalScope;

        // Restore type storage from snapshot
        foreach (var kv in snapshot.Types) _types[kv.Key] = kv.Value;
        foreach (var kv in snapshot.Resolutions) _resolutions[kv.Key] = kv.Value;
        foreach (var kv in snapshot.WrapperResolutions) _wrapperResolutions[kv.Key] = kv.Value;
        foreach (var kv in snapshot.EntitySpecializations) _entitySpecializations[kv.Key] = kv.Value;
        foreach (var kv in snapshot.TypesByShortName) _typesByShortName[kv.Key] = kv.Value;

        // Restore routine storage — list-valued dicts get new lists so test mutations don't leak
        foreach (var kv in snapshot.Routines) _routines[kv.Key] = kv.Value;
        foreach (var kv in snapshot.RoutinesByQualifiedName) _routinesByQualifiedName[kv.Key] = kv.Value;
        foreach (var kv in snapshot.RoutinesByOwner)
            _routinesByOwner[kv.Key] = kv.Value.ToDictionary(
                keySelector: m => m.Key, elementSelector: m => new List<RoutineInfo>(m.Value));
        foreach (var kv in snapshot.RoutineResolutions) _routineResolutions[kv.Key] = kv.Value;

        // Restore preset storage
        foreach (var kv in snapshot.Presets) _presets[kv.Key] = kv.Value;
        foreach (var kv in snapshot.PresetsByQualifiedName) _presetsByQualifiedName[kv.Key] = kv.Value;

        // Restore module tracking
        foreach (var m in snapshot.LoadedModules) _loadedModules.Add(m);
        foreach (var kv in snapshot.ModuleNames) _moduleNames[kv.Key] = kv.Value;

        // Fresh loader per test — shares no mutable state with other test instances.
        // The snapshot's loader is only used to capture the registry state; on-demand module
        // loading (import BuilderQuery, etc.) goes through this new per-test loader.
        // Modules already in _loadedModules (restored from snapshot) are skipped without re-parsing.
        _stdlibPath = snapshot.StdlibRootPath;
        if (snapshot.StdlibRootPath != null)
            _stdlibLoader = new StdlibLoader(stdlibRoot: snapshot.StdlibRootPath, language: language);
        _coreModuleLoaded = true;
    }
}
