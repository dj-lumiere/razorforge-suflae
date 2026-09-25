using System.Text.RegularExpressions;
using Builder.Instantiation;
using Builder.LlvmEmit;
using Builder.Targeting;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Execution;

internal partial class Program
{
    internal static partial class CompileDaemon
    {
        /// <summary>The daemon's resident layers, one per language and build mode.</summary>
        private static readonly Dictionary<string, ResidentLayer> ResidentLayers = new();

        private static ResidentLayer LayerFor(Language language, RfBuildMode buildMode)
        {
            string key = language + "\u0001" + (int)buildMode;
            if (!ResidentLayers.TryGetValue(key: key, value: out ResidentLayer? layer))
            {
                layer = new ResidentLayer(buildMode: buildMode);
                ResidentLayers[key: key] = layer;
            }

            return layer;
        }

        /// <summary>
        /// Stdlib code the resident base does not hold but builds keep needing, AOT-compiled into extra objects
        /// the client loads next to the base. The base comes from one fixed seed program, so a program's own
        /// stdlib instances (Matrix4R4C, Q128, Vector[B32, 4], ...) were collected, emitted and JIT-compiled again
        /// on every edit: ~800 ms per edit for matrix4r4c_api, ~220 ms once they are resident.
        /// <para>After a build whose module defined stdlib routines outside the base and the layers, the daemon
        /// emits exactly those routines as one extra module (user code excluded) and compiles it on a background
        /// thread. The next build that finds it finished adopts it: the collector skips its instances, codegen
        /// declares its symbols, and the client loads its object. One layer is compiled at a time and each is
        /// emitted against the residents of its own build, so no symbol is ever defined twice. Layers live for
        /// the daemon's lifetime (a stdlib or compiler change restarts the daemon).</para>
        /// </summary>
        private sealed class ResidentLayer(RfBuildMode buildMode)
        {
            public List<string> ObjectPaths { get; } = [];
            public HashSet<string> Symbols { get; } = new(comparer: StringComparer.Ordinal);
            public HashSet<string> InstanceKeys { get; } = new(comparer: StringComparer.Ordinal);

            private ObservedBuild? _observed;
            private Task<AdoptableLayer?>? _compiling;

            private sealed record ObservedBuild(LazyJitInputs Inputs, List<string> UserSegments,
                IReadOnlyCollection<string> ResidentSymbols, IReadOnlySet<string> ResidentInstanceKeys,
                IReadOnlyCollection<string> ProgramSymbols);

            private sealed record PendingLayer(string Ir, IReadOnlyCollection<string> Symbols,
                IReadOnlyCollection<string> InstanceKeys);

            private sealed record AdoptableLayer(string ObjectPath, IReadOnlyCollection<string> Symbols,
                IReadOnlyCollection<string> InstanceKeys);

            /// <summary>Takes in a layer whose compile has finished since the last build.</summary>
            public void AdoptFinished()
            {
                if (_compiling is not { IsCompleted: true } done)
                {
                    return;
                }

                _compiling = null;
                if (done.IsCompletedSuccessfully && done.Result is { } layer)
                {
                    ObjectPaths.Add(item: layer.ObjectPath);
                    Symbols.UnionWith(other: layer.Symbols);
                    InstanceKeys.UnionWith(other: layer.InstanceKeys);
                }
            }

            /// <summary>
            /// Looks at a finished build (called inside it, so it only keeps what it needs). When the build's
            /// module defined stdlib routines that are not resident yet, <see cref="StartPendingBuild"/> turns
            /// them into the next layer after the response is sent.
            /// </summary>
            public void Observe(BuildObservation observation, IReadOnlyCollection<string> residentSymbols,
                IReadOnlySet<string> residentInstanceKeys)
            {
                if (_compiling != null)
                {
                    return;
                }

                List<string> userSegments = LazyJitPlanner.UserModuleSegments(
                    userPrograms: observation.Inputs.UserPrograms,
                    entryModule: observation.Inputs.EntryModule);
                if (!observation.DefinedSymbols.Any(predicate: s =>
                        !IsUserDependent(name: s, userSegments: userSegments)))
                {
                    return; // everything the module defined is user code
                }

                _observed = new ObservedBuild(Inputs: observation.Inputs,
                    UserSegments: userSegments,
                    ResidentSymbols: residentSymbols,
                    ResidentInstanceKeys: residentInstanceKeys,
                    ProgramSymbols: observation.DefinedSymbols);
            }

            /// <summary>
            /// Between requests: emits the observed build's non-resident stdlib routines as one module (on the
            /// daemon thread, since emission reads that build's analysis state) and compiles it on a background
            /// thread (external opt/clang runs on files, touching no compiler state).
            /// </summary>
            public void StartPendingBuild()
            {
                if (_observed is not { } observed || _compiling != null)
                {
                    return;
                }

                _observed = null;
                PendingLayer? pending;
                try
                {
                    pending = EmitLayer(inputs: observed.Inputs,
                        userSegments: observed.UserSegments,
                        residentSymbols: observed.ResidentSymbols,
                        residentInstanceKeys: observed.ResidentInstanceKeys,
                        programSymbols: observed.ProgramSymbols);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(value: $"[daemon] resident layer emission failed: {ex.Message}");
                    return;
                }

                if (pending != null)
                {
                    _compiling = Task.Run(function: () => CompileLayer(pending: pending));
                }
            }

            private PendingLayer? EmitLayer(LazyJitInputs inputs, List<string> userSegments,
                IReadOnlyCollection<string> residentSymbols, IReadOnlySet<string> residentInstanceKeys,
                IReadOnlyCollection<string> programSymbols)
            {
                Verification.Results.AnalysisResult r = inputs.Result;
                var userModules = new HashSet<string>(collection: userSegments, comparer: StringComparer.Ordinal);
                var instances = new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal);
                foreach ((string key, MonomorphizedBody body) in r.InstantiatedGenericBodies)
                {
                    if (!residentInstanceKeys.Contains(item: key) &&
                        !UsesUserTypes(routine: body.Info, userModules: userModules))
                    {
                        instances[key: key] = body;
                    }
                }

                var emitter = new LlvmEmitter(userPrograms: [],
                    registry: r.Registry,
                    options: new LlvmEmitterOptions
                    {
                        StdlibPrograms = r.Registry.StdlibPrograms,
                        Target = inputs.Target,
                        BuildMode = inputs.BuildMode,
                        // Only supplies bodies for routines the emitter already decided to define (looked up by
                        // RegistryKey), so it needs no filtering: the instance set and live routines decide that.
                        SynthesizedBodies = r.SynthesizedBodies,
                        InstantiatedGenericBodies = instances,
                        LiveRoutineKeys = r.LiveRoutineKeys,
                        MaySuspendRoutineKeys = r.MaySuspendRoutineKeys,
                        ResidentSymbols = residentSymbols,
                        // Like a delta: references the base's shared trace globals instead of defining them.
                        ForExternalJitModule = true
                    });
                string ir = emitter.Generate();
                IReadOnlyCollection<string> symbols = emitter.GetEmittedRoutineSymbols();
                if (symbols.Count == 0)
                {
                    return null;
                }

                // Never make user code resident: another program with the same module names would link it.
                if (symbols.Any(predicate: s => IsUserDependent(name: s, userSegments: userSegments)))
                {
                    Console.Error.WriteLine(
                        value: "[daemon] resident layer skipped: its module would define user code.");
                    return null;
                }

                // The JIT links an object whole, so one unresolved reference would break every later program.
                // The layer may only use what it defines, what is already resident, and the runtime: a reference
                // to anything else this build's module defined (user code, or stdlib code that stayed out of the
                // layer) means some emitted body depends on this program, so the layer is dropped.
                if (ReferencesProgramCode(ir: ir, layerSymbols: symbols,
                        residentSymbols: residentSymbols, programSymbols: programSymbols))
                {
                    Console.Error.WriteLine(
                        value: "[daemon] resident layer skipped: its code refers to this program's routines.");
                    return null;
                }

                return new PendingLayer(Ir: ir, Symbols: symbols, InstanceKeys: instances.Keys.ToList());
            }

            private AdoptableLayer? CompileLayer(PendingLayer pending)
            {
                try
                {
                    string fingerprint = "layer-" + Convert.ToHexString(
                        inArray: System.Security.Cryptography.SHA256.HashData(
                            source: System.Text.Encoding.UTF8.GetBytes(s: pending.Ir)));
                    string? obj = new BaseObjectCache().GetOrBuild(fingerprint: fingerprint,
                        baseIr: pending.Ir,
                        buildMode: buildMode,
                        wasCached: out _);
                    return obj == null
                        ? null
                        : new AdoptableLayer(ObjectPath: obj, Symbols: pending.Symbols,
                            InstanceKeys: pending.InstanceKeys);
                }
                catch (Exception)
                {
                    // A failed layer is simply never adopted; builds keep defining those routines in the delta.
                    return null;
                }
            }

            /// <summary>True when the routine's owner, type arguments, parameters or return type involve a type
            /// declared in a user module. Names alone miss it: a free generic instance is mangled with the short
            /// type name (<c>Core.hijacked_none(Box)</c>).</summary>
            private static bool UsesUserTypes(RoutineInfo routine, HashSet<string> userModules)
            {
                return IsUserType(type: routine.OwnerType, userModules: userModules) ||
                       IsUserType(type: routine.ReturnType, userModules: userModules) ||
                       (routine.TypeArguments ?? []).Any(predicate: t => IsUserType(type: t, userModules: userModules)) ||
                       routine.Parameters.Any(predicate: p => IsUserType(type: p.Type, userModules: userModules));
            }

            private static bool IsUserType(TypeSymbol? type, HashSet<string> userModules)
            {
                return type switch
                {
                    null => false,
                    _ when type.Module != null && userModules.Contains(item: type.Module) => true,
                    WrapperTypeSymbol wrapper => IsUserType(type: wrapper.InnerType, userModules: userModules),
                    TupleTypeSymbol tuple => tuple.ElementTypes.Any(predicate: e =>
                        IsUserType(type: e, userModules: userModules)),
                    RoutineTypeSymbol routineType =>
                        routineType.ParameterTypes.Any(predicate: p => IsUserType(type: p, userModules: userModules)) ||
                        IsUserType(type: routineType.ReturnType, userModules: userModules),
                    _ => (type.TypeArguments ?? []).Any(predicate: a => IsUserType(type: a, userModules: userModules))
                };
            }

            /// <summary>True when a line of the layer's code (not a <c>declare</c>) names a routine this build's
            /// module defined that is neither in the layer nor already resident.</summary>
            private static bool ReferencesProgramCode(string ir, IReadOnlyCollection<string> layerSymbols,
                IReadOnlyCollection<string> residentSymbols, IReadOnlyCollection<string> programSymbols)
            {
                var available = new HashSet<string>(collection: layerSymbols.Select(selector: Unquote),
                    comparer: StringComparer.Ordinal);
                available.UnionWith(other: residentSymbols.Select(selector: Unquote));
                var program = new HashSet<string>(collection: programSymbols.Select(selector: Unquote),
                    comparer: StringComparer.Ordinal);
                foreach (string line in ir.Split(separator: '\n'))
                {
                    if (line.StartsWith(value: "declare ", comparisonType: StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match m in QuotedSymbol.Matches(input: line))
                    {
                        string symbol = m.Groups[groupnum: 1].Value;
                        if (program.Contains(item: symbol) && !available.Contains(item: symbol))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private static readonly Regex QuotedSymbol =
                new(pattern: "@\"([^\"]+)\"", options: RegexOptions.Compiled);

            private static string Unquote(string symbol)
            {
                return symbol.Trim(trimChar: '"');
            }

            /// <summary>True when a routine name or key mentions a user module, so it depends on user code.</summary>
            private static bool IsUserDependent(string name, List<string> userSegments)
            {
                return userSegments.Any(predicate: seg =>
                    seg.Length > 0 && name.Contains(value: seg, comparisonType: StringComparison.Ordinal));
            }
        }
    }
}
