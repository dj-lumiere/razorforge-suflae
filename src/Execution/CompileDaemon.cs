using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Builder.Targeting;
using Builder.Verification;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>
    /// The warm-compile daemon: a long-lived process that captures the fully-processed stdlib
    /// (<see cref="SemanticVerifier.CompiledStdlibState"/>) in RAM once, then serves build/buildandrun
    /// requests over a per-user named pipe. Each request restores from that snapshot (the ~5 s of stdlib
    /// desugaring/verification/monomorphization is skipped) and only the user program is compiled, so the
    /// edit→result loop drops from ~6.5 s toward the sub-250 ms goal. Nested in <see cref="Program"/> so it
    /// can drive the private <c>BuildExecutable</c> path with a warm-state provider.
    ///
    /// Protocol: length-prefixed (4-byte little-endian) UTF-8 JSON, one request → one response per
    /// connection. The daemon handles requests SERIALLY, which also makes the transient Console
    /// redirection used to capture build diagnostics race-free.
    /// </summary>
    internal static partial class CompileDaemon
    {
        /// <summary>
        /// Detaches the calling process from its console (Windows). An auto-spawned daemon inherits the
        /// short-lived client's console, so it stays in that console's process group — a Ctrl-C in the
        /// developer's shell (or the client's console closing) delivers a CTRL_C/CTRL_CLOSE event to the
        /// WHOLE group, including the daemon, whose <see cref="Console.CancelKeyPress"/> handler then shuts
        /// it down. Calling <c>FreeConsole</c> after redirecting output to the log file removes the daemon
        /// from that group so those console events no longer reach it; it is then stopped only by an explicit
        /// <c>shutdown</c> request (<c>daemon-stop</c>).
        /// </summary>
        [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
        [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
        private static partial bool FreeConsole();

        // ---- request / response DTOs ---------------------------------------------------------------

        private sealed class DaemonRequest
        {
            public string Verb { get; set; } = ""; // "build" | "ir" | "shutdown" | "ping"
            public string EntryFile { get; set; } = "";
            public string? ProjectRoot { get; set; }
            public int BuildMode { get; set; }
            public bool DumpAst { get; set; }
            public bool SaTiming { get; set; }
            public bool RequireStart { get; set; } = true;
            public bool ShowBuildStages { get; set; }
            public List<string> LibraryRoots { get; set; } = [];
            public List<string> CLibraries { get; set; } = [];
            public List<string> LibraryPaths { get; set; } = [];

            /// <summary>Resident-JIT base/delta split (config <c>[target] base-delta</c>): when true the daemon
            /// AOT-compiles the stdlib base to a cached <c>.o</c> ONCE and returns only the small DELTA IR plus
            /// the base object path, so the client loads the object and JITs only the delta.</summary>
            public bool BaseDelta { get; set; }
        }

        private sealed class DaemonResponse
        {
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
            public string? ExePath { get; set; }
            public string? Ir { get; set; }

            /// <summary>Resident-JIT base/delta: path to the cached AOT'd base object the client must load
            /// before JIT-ing <see cref="Ir"/> (the delta). Null on the normal full-IR path.</summary>
            public string? BaseObjectPath { get; set; }

            /// <summary>Server-side wall-clock ms of the warm compile itself (excludes IPC/transfer), so a
            /// client can report where dev-loop latency goes: round-trip − CompileMs = transfer.</summary>
            public long CompileMs { get; set; }
        }

        // ---- pipe naming / opt-in ------------------------------------------------------------------

        /// <summary>Per-user pipe name so two users on one machine don't collide. Overridable via
        /// <c>RAZORFORGE_DAEMON_PIPE</c> (e.g. to run more than one daemon).</summary>
        private static string PipeName()
        {
            string? overridden =
                Environment.GetEnvironmentVariable(variable: "RAZORFORGE_DAEMON_PIPE");
            if (!string.IsNullOrWhiteSpace(value: overridden))
            {
                return overridden;
            }

            string user = Environment.UserName;
            return $"razorforge-daemon-{user}";
        }

        // Client daemon-routing (manifest <c>[target] use-daemon</c>) and JIT (mode <c>debug-jit</c>) are read
        // straight off the ResolvedEntry at each call site now — no env vars.

        /// <summary>Set by the builder on a daemon it AUTO-SPAWNS (value = log-file path). The spawned daemon
        /// redirects its own console to that file so it is fully detached from the short-lived client that
        /// started it. Absent for a daemon a developer starts by hand (keeps writing to its own terminal).</summary>
        private const string DaemonLogEnvVar = "RAZORFORGE_DAEMON_LOG";

        /// <summary><c>[debug] timing</c> gates the per-phase <c>[timing]</c> diagnostics used while profiling
        /// the dev loop; off by default so normal runs are quiet.</summary>
        private static bool PhaseTiming()
        {
            return Builder.Diagnostics.DiagnosticFlags.PhaseTiming;
        }

        // ---- server --------------------------------------------------------------------------------

        private static readonly Dictionary<Language, SemanticVerifier.CompiledStdlibState>
            WarmCache = new();

        /// <summary>Lazily captures (and caches) the fully-processed stdlib for a language. The first
        /// request per language pays the ~5 s capture; every request after is warm.</summary>
        private static SemanticVerifier.CompiledStdlibState GetWarm(Language language)
        {
            if (WarmCache.TryGetValue(key: language,
                    value: out SemanticVerifier.CompiledStdlibState? cached))
            {
                return cached;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine(value: $"[daemon] loading {language} stdlib snapshot...");
            // Load from the on-disk .pbrf cache when the stdlib+compiler hash matches (~1–2 s), else capture
            // fresh (~5–8 s) and write the cache for next startup.
            SemanticVerifier.CompiledStdlibState state =
                Builder.Serialization.StdlibSnapshotCache.LoadOrCapture(language: language,
                    log: msg => Console.Error.WriteLine(value: $"[daemon] {msg}"));
            WarmCache[key: language] = state;
            Console.Error.WriteLine(
                value: $"[daemon] {language} stdlib snapshot ready ({sw.ElapsedMilliseconds} ms)");
            return state;
        }

        /// <summary>Daemon-lifetime cache of the stdlib import index per (language, library-roots). The index
        /// is invariant across warm requests (stdlib source + host target are fixed), so building it once and
        /// re-seeding it replaces the ~0.8 s per-request stdlib re-parse the BuildDriver otherwise repeats.</summary>
        private static readonly Dictionary<string, IReadOnlyDictionary<string, string>>
            StdlibIndexCache = new();

        private static IReadOnlyDictionary<string, string>? GetStdlibIndex(Language language,
            IReadOnlyList<string> libraryRoots)
        {
            // Key on language + the ordered library-root set: distinct [target] library sets index different
            // module surfaces. (Library roots are re-registered per request on top of the seeded stdlib index.)
            string key = language + "\u0001" +
                         string.Join(separator: "\u0001", values: libraryRoots);
            if (StdlibIndexCache.TryGetValue(key: key,
                    value: out IReadOnlyDictionary<string, string>? cached))
            {
                return cached;
            }

            IReadOnlyDictionary<string, string> index =
                Builder.Declaration.BuildDriver.BuildStdlibIndex(
                    stdlibRoot: Builder.Declaration.StdlibLoader.GetDefaultStdlibPath(),
                    language: language,
                    libraryRoots: libraryRoots);
            StdlibIndexCache[key: key] = index;
            return index;
        }

        /// <summary>Daemon-lifetime cache of the resident-JIT BASE artifact (AOT'd stdlib object path + the
        /// base's DEFINED-symbol set) per (language, build mode).</summary>
        private static readonly Dictionary<string, (string ObjPath, IReadOnlyCollection<string> Syms)>
            BaseArtifactCache = new();

        /// <summary>
        /// Builds (ONCE per language+mode, then daemon- and disk-cached) the resident-JIT base: a full
        /// <c>SeedAllStdlibRoutines</c> analysis → <c>GenerateBase</c> (non-pruned stdlib IR, no <c>@main</c>)
        /// → AOT-compiled <c>.o</c> via <see cref="BaseObjectCache"/>. Returns the object path plus the base's
        /// DEFINED-symbol set — the <c>residentSymbols</c> the per-run DELTA emission extern-declares instead
        /// of re-defining. Returns null on failure so the caller falls back to shipping the full IR.
        /// </summary>
        private static (string ObjPath, IReadOnlyCollection<string> Syms)? EnsureBaseArtifact(
            Language language, RfBuildMode buildMode)
        {
            string key = language + "" + (int)buildMode;
            if (BaseArtifactCache.TryGetValue(key: key,
                    value: out (string ObjPath, IReadOnlyCollection<string> Syms) cached))
            {
                return cached;
            }

            string? fp = Builder.Serialization.StdlibSnapshotCache.ComputeStdlibHash(language: language);
            if (fp == null)
            {
                return null;
            }

            fp = fp + "-" + (int)buildMode;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine(value: $"[daemon] building resident-JIT base ({language})...");
            try
            {
                var baseSa = new SemanticVerifier(language: language)
                {
                    SeedAllStdlibRoutines = true
                };
                System.Collections.Generic.List<Builder.Tokenizer.Token> toks =
                    new Builder.Tokenizer.Tokenizer(source: "module Base\nroutine start()\n  return",
                        fileName: "base.rf", language: language).Tokenize();
                Builder.Verification.Results.AnalysisResult baseR = baseSa.Analyze(
                    program: new Builder.Parser.Parser(tokens: toks, language: language,
                        fileName: "base.rf").Parse());
                if (baseR.Errors.Count > 0)
                {
                    return null;
                }

                Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
                    programs: baseR.Registry.UserPrograms,
                    instantiatedBodies: baseR.InstantiatedGenericBodies,
                    maySuspendKeys: baseR.MaySuspendRoutineKeys,
                    registry: baseR.Registry);
                var baseGen = new Builder.LlvmEmit.LlvmEmitter(
                    userPrograms: new System.Collections.Generic.List<(SyntaxTree.Program, string,
                        string)>(),
                    registry: baseR.Registry,
                    options: new Builder.LlvmEmit.LlvmEmitterOptions
                    {
                        StdlibPrograms = baseR.Registry.StdlibPrograms,
                        SynthesizedBodies = baseR.SynthesizedBodies,
                        InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
                    });
                (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

                string? obj = new BaseObjectCache().GetOrBuild(fingerprint: fp,
                    baseIr: baseIr,
                    buildMode: buildMode,
                    wasCached: out bool wasCached);
                if (obj == null)
                {
                    return null;
                }

                Console.Error.WriteLine(
                    value:
                    $"[daemon] resident-JIT base ready: {baseSyms.Count} syms, obj-cached={wasCached} ({sw.ElapsedMilliseconds} ms)");
                (string ObjPath, IReadOnlyCollection<string> Syms) result = (obj, baseSyms);
                BaseArtifactCache[key: key] = result;
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"[daemon] resident-JIT base build failed: {ex.Message}");
                return null;
            }
        }

        private static volatile bool _shutdownRequested;

        /// <summary>Runs the daemon server loop (foreground) until a shutdown request or Ctrl-C.</summary>
        public static int RunServer()
        {
            // When auto-spawned by the builder, redirect our console to a log file up front so we are fully
            // detached from the (short-lived) client that started us — its console can close without our
            // subsequent per-request writes throwing on a broken pipe.
            string? logPath = Environment.GetEnvironmentVariable(variable: DaemonLogEnvVar);
            bool autoSpawned = !string.IsNullOrEmpty(value: logPath);
            if (autoSpawned)
            {
                DetachAutoSpawnedConsole(logPath: logPath!);
            }

            string pipe = PipeName();
            // The compiler DLL mtime THIS daemon loaded at startup. A client compares it (via the version-
            // stamped pong) to the CURRENT on-disk mtime; a `dotnet build` that rewrote the DLL makes them
            // differ, so the client knows this daemon is running stale code (old lowering/warm snapshot →
            // e.g. an unlowered GMCE at codegen) and restarts it instead of reusing it.
            string daemonStamp = CompilerVersionStamp();
            Console.Error.WriteLine(
                value: $"[daemon] razorforge warm-compile daemon on pipe '{pipe}'");
            Console.Error.WriteLine(
                value:
                "[daemon] routed builds arrive from clients with [target] use-daemon = true.");

            // Pre-warm the primary language so the first real build is already warm.
            try
            {
                _ = GetWarm(language: InvokedAsSuflae
                    ? Language.Suflae
                    : Language.RazorForge);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"[daemon] pre-warm failed: {ex.Message}");
            }

            // Only a FOREGROUND (hand-started) daemon still owns a console and should shut down on Ctrl-C.
            // An auto-spawned daemon has FreeConsole'd above, so it has no console to receive Ctrl-C from —
            // registering the handler would be a no-op and (pre-FreeConsole) was the accidental-shutdown path.
            if (!autoSpawned)
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    _shutdownRequested = true;
                    e.Cancel = true;
                };
            }

            while (!_shutdownRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName: pipe,
                        direction: PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        transmissionMode: PipeTransmissionMode.Byte,
                        options: PipeOptions.None);
                    server.WaitForConnection();
                    ServeConnection(server: server, daemonStamp: daemonStamp);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(value: $"[daemon] request failed: {ex.Message}");
                }
            }

            Console.Error.WriteLine(value: "[daemon] shutting down.");
            return 0;
        }

        /// <summary>Redirects an auto-spawned daemon's console to its log file and (on Windows) detaches it
        /// from the client's console process group, so a Ctrl-C in the developer's shell (or the client's
        /// console closing after one build) does NOT deliver a console CTRL event to this daemon and shut it
        /// down — the bug where the daemon died and re-spawned on every buildandrun. An auto-spawned daemon is
        /// stopped only by an explicit `shutdown` request (daemon-stop).</summary>
        private static void DetachAutoSpawnedConsole(string logPath)
        {
            try
            {
                var logWriter =
                    new StreamWriter(path: logPath, append: false) { AutoFlush = true };
                Console.SetOut(newOut: logWriter);
                Console.SetError(newError: logWriter);
            }
            catch
            {
                // Couldn't open the log file — keep the inherited console rather than failing to start.
            }

            if (OperatingSystem.IsWindows())
            {
                try { FreeConsole(); }
                catch
                {
                    /* best-effort: worst case, the pre-fix console-shared behavior */
                }
            }
        }

        /// <summary>Reads one framed request off an accepted connection and writes its response. Handles the
        /// <c>shutdown</c>/<c>ping</c> control verbs inline and routes <c>ir</c>/<c>build</c> to their handlers.</summary>
        private static void ServeConnection(NamedPipeServerStream server, string daemonStamp)
        {
            DaemonRequest? req = ReadMessage<DaemonRequest>(stream: server);
            if (req == null)
            {
                return;
            }

            if (req.Verb == "shutdown")
            {
                WriteMessage(stream: server,
                    value: new DaemonResponse { ExitCode = 0, Output = "" });
                _shutdownRequested = true;
                return;
            }

            if (req.Verb == "ping")
            {
                WriteMessage(stream: server,
                    value: new DaemonResponse { ExitCode = 0, Output = $"pong {daemonStamp}" });
                return;
            }

            DaemonResponse resp = req.Verb == "ir"
                ? HandleIr(req: req)
                : HandleBuild(req: req);
            WriteMessage(stream: server, value: resp);
        }

        /// <summary>Runs one warm build, capturing all build diagnostics (Console.Out + Console.Error, in
        /// chronological order via a single writer) so they can be shipped back to the client.</summary>
        private static DaemonResponse HandleBuild(DaemonRequest req)
        {
            // Per-request diagnostic flags from the client's manifest (the daemon is project-less and serves
            // serially, so reset-then-set is race-free). `timing` = the merged sa/phase flag (req.SaTiming).
            Builder.Diagnostics.DiagnosticFlags.Reset();
            Builder.Diagnostics.DiagnosticFlags.PhaseTiming = req.SaTiming;
            var captured = new StringWriter();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TextWriter savedOut = Console.Out;
            TextWriter savedErr = Console.Error;
            int exit;
            string exePath;
            try
            {
                Console.SetOut(newOut: captured);
                Console.SetError(newError: captured);
                exit = BuildExecutable(entryFile: req.EntryFile,
                    exeFile: out exePath,
                    config: new ResolvedEntry
                    {
                        ProjectRoot = req.ProjectRoot,
                        BuildMode = (RfBuildMode)req.BuildMode,
                        DumpAst = req.DumpAst,
                        SaTiming = req.SaTiming,
                        RequireStartRoutine = req.RequireStart,
                        ShowBuildStages = req.ShowBuildStages,
                        LibraryRoots = req.LibraryRoots,
                        CLibraries = req.CLibraries,
                        LibraryPaths = req.LibraryPaths
                    },
                    warmProvider: GetWarm);
            }
            catch (Exception ex)
            {
                captured.WriteLine(value: $"[daemon] build threw: {ex.Message}");
                exit = 1;
                exePath = "";
            }
            finally
            {
                Console.SetOut(newOut: savedOut);
                Console.SetError(newError: savedErr);
            }

            sw.Stop();
            Console.Error.WriteLine(
                value:
                $"[daemon] {Path.GetFileName(path: req.EntryFile)} -> exit {exit} ({sw.ElapsedMilliseconds} ms)");
            return new DaemonResponse
            {
                ExitCode = exit,
                Output = captured.ToString(),
                ExePath = exit == 0
                    ? exePath
                    : null,
                CompileMs = sw.ElapsedMilliseconds
            };
        }

        /// <summary>Runs one warm compile-to-IR (no opt/clang/link), for the ORC-JIT client path. Captures
        /// build diagnostics and returns the IR text so the client can JIT-and-run it in-process.</summary>
        private static DaemonResponse HandleIr(DaemonRequest req)
        {
            Builder.Diagnostics.DiagnosticFlags.Reset();
            Builder.Diagnostics.DiagnosticFlags.PhaseTiming = req.SaTiming;
            var captured = new StringWriter();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TextWriter savedOut = Console.Out;
            TextWriter savedErr = Console.Error;
            int exit;
            string ir;
            string? baseObjectPath = null;
            try
            {
                Console.SetOut(newOut: captured);
                Console.SetError(newError: captured);
                // Resident-JIT base/delta: AOT the stdlib base once (cached), then emit only the DELTA IR
                // (residentSymbols = the base's defined syms ⇒ codegen extern-declares them). Falls back to
                // full IR if the base can't be built, so the client path stays correct either way.
                IReadOnlyCollection<string>? residentSymbols = null;
                if (req.BaseDelta)
                {
                    Language lang = InvokedAsSuflae
                        ? Language.Suflae
                        : Language.RazorForge;
                    (string ObjPath, IReadOnlyCollection<string> Syms)? baseArtifact =
                        EnsureBaseArtifact(language: lang, buildMode: (RfBuildMode)req.BuildMode);
                    if (baseArtifact is { } ba)
                    {
                        residentSymbols = ba.Syms;
                        baseObjectPath = ba.ObjPath;
                    }
                }

                exit = BuildToIr(entryFile: req.EntryFile,
                    ir: out ir,
                    config: new ResolvedEntry
                    {
                        ProjectRoot = req.ProjectRoot,
                        BuildMode = (RfBuildMode)req.BuildMode,
                        RequireStartRoutine = req.RequireStart,
                        LibraryRoots = req.LibraryRoots
                    },
                    warm: new WarmProviders(WarmProvider: GetWarm,
                        IrCallback: null,
                        StdlibIndexProvider: GetStdlibIndex,
                        ResidentSymbols: residentSymbols));
            }
            catch (Exception ex)
            {
                captured.WriteLine(value: $"[daemon] compile-to-IR threw: {ex.Message}");
                exit = 1;
                ir = "";
            }
            finally
            {
                Console.SetOut(newOut: savedOut);
                Console.SetError(newError: savedErr);
            }

            sw.Stop();
            Console.Error.WriteLine(
                value:
                $"[daemon] ir {Path.GetFileName(path: req.EntryFile)} -> exit {exit} ({ir.Length} chars, {sw.ElapsedMilliseconds} ms)");
            return new DaemonResponse
            {
                ExitCode = exit,
                Output = captured.ToString(),
                CompileMs = sw.ElapsedMilliseconds,
                Ir = exit == 0
                    ? ir
                    : null,
                BaseObjectPath = exit == 0
                    ? baseObjectPath
                    : null
            };
        }

        /// <summary>Connects, sends <c>shutdown</c>, and returns 0 on success (used by <c>daemon-stop</c>).</summary>
        public static int StopServer()
        {
            try
            {
                DaemonResponse? _ = SendRequest(request: new DaemonRequest { Verb = "shutdown" },
                    timeoutMs: 2000);
                Console.WriteLine(value: "Daemon stop requested.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"No running daemon to stop ({ex.Message}).");
                return 1;
            }
        }

        // ---- client --------------------------------------------------------------------------------

        /// <summary>Client entry for the <c>build</c> verb: delegates the compile to a running daemon.
        /// Returns false (so the caller falls back to a cold build) when the daemon is disabled,
        /// unreachable, or the target uses unsupported [libraries.*] configs.</summary>
        internal static bool TryClientBuild(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            if (!TryClientCompile(resolved: resolved, response: out DaemonResponse? resp))
            {
                return false;
            }

            Console.Write(value: resp!.Output);
            if (PhaseTiming())
            {
                Console.Error.WriteLine(
                    value: $"[timing] daemon warm compile: {resp.CompileMs} ms");
            }

            if (resp.ExitCode == 0 && resp.ExePath != null)
            {
                Console.WriteLine(
                    value: $"Executable written to: {Path.GetFullPath(path: resp.ExePath)}");
            }

            exitCode = resp.ExitCode;
            return true;
        }

        /// <summary>Client entry for <c>buildandrun</c>: the daemon performs the warm COMPILE and returns
        /// the exe path; this process runs it locally so interactive stdin/stdout stays here.</summary>
        internal static bool TryClientBuildAndRun(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            if (!TryClientCompile(resolved: resolved, response: out DaemonResponse? resp))
            {
                return false;
            }

            Console.Write(value: resp!.Output);
            if (PhaseTiming())
            {
                Console.Error.WriteLine(
                    value: $"[timing] daemon warm compile: {resp.CompileMs} ms");
            }

            if (resp.ExitCode != 0 || resp.ExePath == null)
            {
                exitCode = resp.ExitCode;
                return true;
            }

            exitCode = RunExecutable(exeFile: resp.ExePath,
                showBuildStages: resolved.ShowBuildStages);
            return true;
        }

        /// <summary>Client entry for the ORC-JIT dev loop: obtain the module IR (warm from the daemon when
        /// enabled+reachable, else a local cold compile) and JIT-and-run it in THIS process, so program
        /// stdio is the user's terminal. Returns false to fall back to the AOT build path when JIT is
        /// disabled or the JIT runtime can't initialize (e.g. libLLVM missing). A build error still counts
        /// as "handled" (returns true with the error's exit code — no point falling back to a build that
        /// will fail identically).</summary>
        internal static bool TryClientJitRun(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            if (!resolved.Jit || resolved.EntryFile == null)
            {
                return false;
            }

            // Rich [libraries.NAME] targets aren't carried by the daemon IR path, and a JIT'd module can't
            // link arbitrary user C libraries via the simple process-search generator — leave those to AOT.
            if (resolved.LibraryConfigs.Count > 0 || resolved.CLibraries.Count > 0)
            {
                return false;
            }

            if (!OrcJitExecutor.TryInitialize(error: out string? initErr))
            {
                Console.Error.WriteLine(
                    value: $"[jit] unavailable ({initErr}); falling back to AOT build.");
                return false;
            }

            // Resident-JIT incremental (B) M3: `[target] incremental` runs the program via the FULLY-LAZY JIT +
            // per-routine disk IR cache instead of a single eager module — @main is materialized on demand and
            // each pure-stdlib routine is served from / written to the cross-run cache. Analysis is still
            // in-process here (the cross-run analysis-reuse is a separate Phase-2 concern); the win is skipping
            // codegen + JIT of routines already cached from a previous run.
            if (resolved.Incremental)
            {
                return TryClientJitRunIncremental(resolved: resolved, exitCode: out exitCode);
            }

            // Obtain IR: warm via the daemon when opted-in and reachable, else a cold in-process compile.
            string ir = "";
            int rc = 0;
            bool haveIr = resolved.UseDaemon &&
                          TryGetWarmDaemonIr(resolved: resolved, ir: out ir, exitCode: out rc,
                              baseObjectPath: out _);
            if (!haveIr)
            {
                rc = BuildToIr(entryFile: Path.GetFullPath(path: resolved.EntryFile),
                    ir: out ir,
                    config: resolved);
            }

            if (rc != 0 || string.IsNullOrEmpty(value: ir))
            {
                exitCode = rc != 0
                    ? rc
                    : 1;
                return true;
            }

            try
            {
                var _swJit = System.Diagnostics.Stopwatch.StartNew();
                exitCode = OrcJitExecutor.JitAndRun(llvmIr: ir,
                    programName: Path.GetFullPath(path: resolved.EntryFile),
                    programArgs: []);
                _swJit.Stop();
                if (PhaseTiming())
                {
                    Console.Error.WriteLine(
                        value: $"[timing] JIT compile + run: {_swJit.ElapsedMilliseconds} ms");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"[jit] execution failed: {ex.Message}");
                exitCode = 1;
                return true;
            }
        }

        /// <summary>The `[target] incremental` JIT path (resident-JIT incremental (B) M3): analyze in-process,
        /// then JIT-run @main with a fully-lazy ORC generator that materializes each reached routine on demand
        /// — served from / written to the per-routine disk IR cache (keyed by stdlib+compiler fingerprint), so
        /// a re-run skips codegen + JIT of every routine already cached. Returns true (handled) with the exit
        /// code; a build error is handled (no AOT fallback — it would fail identically).</summary>
        private static bool TryClientJitRunIncremental(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            Language lang = InvokedAsSuflae
                ? Language.Suflae
                : Language.RazorForge;
            string entryFull = Path.GetFullPath(path: resolved.EntryFile!);

            // Resident-JIT (A) split-at-IR: prefer the WARM DAEMON. It holds the analyzed stdlib snapshot in
            // RAM (captured once at startup), so routing the analysis THERE removes the ~840 ms per-run `.pbrf`
            // reload the in-process path below pays in this disposable client. The daemon compiles warm and
            // ships IR; THIS throwaway client only JITs+runs it, so a user crash (rf_crash → exit(1)) kills only
            // the client and never the warm daemon (§3 crash isolation preserved). Falls through to the
            // in-process lazy path when no daemon is reachable.
            if (resolved.UseDaemon &&
                TryGetWarmDaemonIr(resolved: resolved, ir: out string daemonIr,
                    exitCode: out int daemonRc, baseObjectPath: out string? daemonBaseObj,
                    allowBaseDelta: true))
            {
                if (daemonRc != 0)
                {
                    exitCode = daemonRc;
                    return true;
                }

                try
                {
                    var swJit = System.Diagnostics.Stopwatch.StartNew();
                    // Base/delta: load the AOT'd stdlib base object + JIT only the delta. Full-IR fallback
                    // when the daemon shipped no base object (base build failed, or base-delta disabled).
                    exitCode = daemonBaseObj != null
                        ? OrcJitExecutor.JitAndRunSplitWithBaseObject(baseObjectPath: daemonBaseObj,
                            deltaIr: daemonIr,
                            programName: entryFull,
                            programArgs: [])
                        : OrcJitExecutor.JitAndRun(llvmIr: daemonIr,
                            programName: entryFull,
                            programArgs: []);
                    swJit.Stop();
                    if (PhaseTiming())
                    {
                        Console.Error.WriteLine(
                            value:
                            $"[timing] JIT compile + run (daemon IR{(daemonBaseObj != null ? ", base/delta" : "")}): {swJit.ElapsedMilliseconds} ms");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(value: $"[jit] incremental (daemon) execution failed: {ex.Message}");
                    exitCode = 1;
                    return true;
                }
            }

            // The IR-cache fingerprint is stdlib+compiler-only (program-INVARIANT), so it is known before the
            // program is analyzed. Build a predicate cache now and hand its Phase-9 skip predicate to the WARM
            // analysis: an instance whose IR is already cached from a prior run will be an M2b codegen HIT this
            // run (never re-emitted), so its backend-repr+validate (the ~440 ms PostDesugarChecks bulk) is
            // redundant and skipped. (Empty user segments: the predicate only probes file existence, and PathFor
            // ignores segments — a user-dependent instance's IR is never written by Build's real-segs cache, so
            // its file never exists and the predicate never skips it. See RoutineIrCache.Has.)
            string fingerprint =
                Builder.Serialization.StdlibSnapshotCache.ComputeStdlibHash(language: lang) ?? "nofp";
            var predicateCache =
                new RoutineIrCache(fingerprint: fingerprint, userModuleSegments: Array.Empty<string>());

            // Use the WARM stdlib (GetWarm loads the .pbrf snapshot in ~1-2 s, or captures once), so the
            // in-process analysis is constructed WARM (IsWarm=true) — PreRegisterStdlibVariants + the stdlib
            // program repr in PostDesugarChecks are skipped (already in the restored snapshot), instead of the
            // cold path re-doing that stdlib-invariant work every run.
            int rc = BuildToLazyJitInputs(entryFile: entryFull,
                inputs: out LazyJitInputs? inputs,
                config: resolved,
                warm: new WarmProviders(WarmProvider: GetWarm,
                    IrCallback: null,
                    StdlibIndexProvider: null,
                    InstanceCheckSkip: LazyJitPlanner.IrCachedPredicate(cache: predicateCache)));
            if (rc != 0 || inputs == null)
            {
                exitCode = rc != 0
                    ? rc
                    : 1;
                return true;
            }

            List<string> segs =
                LazyJitPlanner.UserModuleSegments(userPrograms: inputs.UserPrograms,
                    entryModule: inputs.EntryModule);
            var cache = new RoutineIrCache(fingerprint: fingerprint, userModuleSegments: segs);
            (string mainIr, Func<string, string?> materialize) =
                LazyJitPlanner.Build(inputs: inputs, cache: cache);

            try
            {
                var swJit = System.Diagnostics.Stopwatch.StartNew();
                exitCode = OrcJitExecutor.JitAndRunLazy(mainIr: mainIr,
                    materialize: materialize,
                    programName: entryFull,
                    programArgs: []);
                swJit.Stop();
                if (PhaseTiming())
                {
                    Console.Error.WriteLine(
                        value:
                        $"[timing] incremental JIT (cache: {cache.Hits} reused, {cache.Misses} fresh, {cache.Uncacheable} user): {swJit.ElapsedMilliseconds} ms");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"[jit] incremental execution failed: {ex.Message}");
                exitCode = 1;
                return true;
            }
        }

        /// <summary>Ensures a warm daemon is up, then fetches the module IR from it, reporting the one-time
        /// spawn+warm wait and the IPC-transfer timing separately. Returns false (cold fallback) when the
        /// daemon can't be reached or the fetch fails.</summary>
        private static bool TryGetWarmDaemonIr(ResolvedEntry resolved, out string ir,
            out int exitCode, out string? baseObjectPath, bool allowBaseDelta = false)
        {
            ir = "";
            exitCode = 0;
            baseObjectPath = null;
            if (!EnsureDaemonRunning(warmWaitMs: out long warmWaitMs, spawned: out bool spawned))
            {
                return false;
            }

            // The one-time spawn+warm wait (~5 s on first run) is NOT transfer — report it separately so
            // it doesn't inflate the `transfer` figure below.
            if (PhaseTiming() && spawned)
            {
                Console.Error.WriteLine(value: $"[timing] daemon spawn+warm: {warmWaitMs} ms");
            }

            // Start the transfer clock only NOW (daemon confirmed warm), so `transfer` = round-trip −
            // server compile, the real IPC cost, not the spawn+warm wait.
            var _swIr = System.Diagnostics.Stopwatch.StartNew();
            if (!TryDaemonIr(resolved: resolved,
                    ir: out ir,
                    exitCode: out exitCode,
                    serverCompileMs: out long serverCompileMs,
                    baseObjectPath: out baseObjectPath,
                    allowBaseDelta: allowBaseDelta))
            {
                return false;
            }

            _swIr.Stop();
            if (PhaseTiming())
            {
                Console.Error.WriteLine(
                    value:
                    $"[timing] daemon IR fetch: {_swIr.ElapsedMilliseconds} ms (server compile {serverCompileMs} ms, transfer {_swIr.ElapsedMilliseconds - serverCompileMs} ms, {ir.Length} chars)");
            }

            return true;
        }

        /// <summary>Requests warm IR from a running daemon. Returns false (cold fallback) when the daemon is
        /// unreachable. On success, writes the daemon's captured build diagnostics to the console and yields
        /// the IR + build exit code.</summary>
        private static bool TryDaemonIr(ResolvedEntry resolved, out string ir, out int exitCode,
            out long serverCompileMs, out string? baseObjectPath, bool allowBaseDelta = false)
        {
            ir = "";
            exitCode = 0;
            serverCompileMs = 0;
            baseObjectPath = null;
            var req = new DaemonRequest
            {
                Verb = "ir",
                EntryFile = Path.GetFullPath(path: resolved.EntryFile!),
                ProjectRoot = resolved.ProjectRoot,
                BuildMode = (int)resolved.BuildMode,
                RequireStart = resolved.RequireStartRoutine,
                SaTiming = resolved.SaTiming,
                LibraryRoots = [.. resolved.LibraryRoots],
                // base/delta ONLY on the incremental JIT path (it loads the base object); the plain full-IR
                // JitAndRun caller must NOT request a delta (it can't resolve the base's extern symbols).
                BaseDelta = resolved.BaseDelta && allowBaseDelta
            };
            try
            {
                DaemonResponse? resp = SendRequest(request: req, timeoutMs: 500);
                if (resp == null)
                {
                    return false;
                }

                Console.Write(value: resp.Output);
                ir = resp.Ir ?? "";
                exitCode = resp.ExitCode;
                serverCompileMs = resp.CompileMs;
                baseObjectPath = resp.BaseObjectPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Shared client compile: gate on opt-in + supported config, connect, exchange one
        /// request/response. Returns false to signal a cold fallback (never throws for the caller).</summary>
        private static bool TryClientCompile(ResolvedEntry resolved, out DaemonResponse? response)
        {
            response = null;
            if (!resolved.UseDaemon || resolved.EntryFile == null)
            {
                return false;
            }

            // The daemon protocol doesn't carry rich [libraries.NAME] configs yet — such targets stay cold.
            if (resolved.LibraryConfigs.Count > 0)
            {
                return false;
            }

            // Auto-start the daemon on first use (and reuse it thereafter); cold fallback if it won't come up.
            if (!EnsureDaemonRunning(warmWaitMs: out long warmWaitMs, spawned: out bool spawned))
            {
                return false;
            }

            // Report the one-time spawn+warm wait on its own line (the build path's `daemon warm compile` is
            // server-measured and already excludes it, but without this the ~5 s first-run cost is invisible).
            if (PhaseTiming() && spawned)
            {
                Console.Error.WriteLine(value: $"[timing] daemon spawn+warm: {warmWaitMs} ms");
            }

            var req = new DaemonRequest
            {
                Verb = "build",
                EntryFile = Path.GetFullPath(path: resolved.EntryFile),
                ProjectRoot = resolved.ProjectRoot,
                BuildMode = (int)resolved.BuildMode,
                DumpAst = resolved.DumpAst,
                SaTiming = resolved.SaTiming,
                RequireStart = resolved.RequireStartRoutine,
                ShowBuildStages = resolved.ShowBuildStages,
                LibraryRoots = [.. resolved.LibraryRoots],
                CLibraries = [.. resolved.CLibraries],
                LibraryPaths = [.. resolved.LibraryPaths]
            };

            try
            {
                response = SendRequest(request: req, timeoutMs: 500);
                return response != null;
            }
            catch
            {
                // No daemon listening (or it died mid-request) — fall back to a cold in-process build.
                return false;
            }
        }

        /// <summary>Response-read timeout (ms). Generous — a warm daemon answers in ~1-2 s, a cold-snapshot
        /// first build in a few — but a WEDGED daemon that connects yet never frames a response must not hang
        /// the client forever. That was the `build` stall: <see cref="TryClientCompile"/>'s cold fallback only
        /// fires when this method RETURNS, and an unbounded pipe read never returns. On timeout we return null,
        /// so the caller falls back to a cold in-process build — the safety net the JIT IR path already had
        /// (its daemon fetch is optional, with an immediate cold branch), now extended to the exe path.</summary>
        private const int ResponseReadTimeoutMs = 30_000;

        private static DaemonResponse? SendRequest(DaemonRequest request, int timeoutMs)
        {
            bool trace = Environment.GetEnvironmentVariable(variable: "RF_DAEMON_TRACE") is { Length: > 0 };
            void T(string m)
            {
                if (trace) { Console.Error.WriteLine(value: $"[daemon-client] {m}"); }
            }

            using var client = new NamedPipeClientStream(serverName: ".",
                pipeName: PipeName(),
                direction: PipeDirection.InOut,
                options: PipeOptions.None);
            T(m: $"connecting (verb={request.Verb}, connect-timeout={timeoutMs}ms)...");
            client.Connect(timeout: timeoutMs);
            T(m: "connected; writing request...");
            WriteMessage(stream: client, value: request);
            T(m: "request written+flushed; reading response...");

            // A NamedPipe read ignores its CancellationToken once pending, so bound the response read with
            // a worker task and force-unblock a stuck read by DISPOSING the stream on timeout (the reliable
            // way to abort a blocked pipe read). Timeout -> null -> caller falls back to a cold build.
            var readTask = System.Threading.Tasks.Task.Run(
                function: () => ReadMessage<DaemonResponse>(stream: client));
            if (!readTask.Wait(millisecondsTimeout: ResponseReadTimeoutMs))
            {
                T(m: $"response read TIMED OUT after {ResponseReadTimeoutMs}ms -> abandoning daemon, cold build");
                try { client.Dispose(); } catch { /* unblocks the worker's Read */ }
                return null;
            }

            if (readTask.IsFaulted)
            {
                T(m: $"response read FAULTED: {readTask.Exception?.InnerException?.Message}");
                return null;
            }

            T(m: $"response read complete (exit={readTask.Result?.ExitCode})");
            return readTask.Result;
        }

        // ---- auto-spawn (manifest `[target] use-daemon = true` -> the builder manages the daemon) -----

        /// <summary>The compiler DLL's on-disk mtime ticks — a cheap version stamp. The daemon captures it at
        /// startup and returns it in the pong; a client computes it fresh, so a `dotnet build` that rewrote
        /// the DLL makes them differ (the daemon is running stale code + a stale warm snapshot).</summary>
        private static string CompilerVersionStamp()
        {
            long ticks = 0;
            try
            {
                string? loc = System.Reflection.Assembly.GetEntryAssembly()
                                   ?.Location;
                if (!string.IsNullOrEmpty(value: loc) && File.Exists(path: loc))
                {
                    ticks = new FileInfo(fileName: loc).LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                /* best-effort */
            }

            // Fold the stdlib source tree's NEWEST mtime into the stamp. A `dotnet build` that only recopied
            // stdlib `.rf`/`.sf` (no C# change) leaves the DLL mtime untouched, so a DLL-mtime-only stamp
            // would let a running daemon keep serving a STALE stdlib snapshot (the phantom "routine X not
            // defined" after editing stdlib). Bumping the stamp on any stdlib edit makes the existing
            // stale-daemon restart path (TryPing mismatch -> ShutdownStaleDaemonIfPresent -> respawn) fire.
            // Both client (fresh each ping) and daemon (captured at startup) resolve the same root, so the
            // values agree until a file actually changes. Best-effort: a walk failure just omits stdlib.
            try
            {
                string stdlibRoot = Builder.Declaration.StdlibLoader.GetDefaultStdlibPath();
                if (Directory.Exists(path: stdlibRoot))
                {
                    ticks = NewestMtimeTicks(root: stdlibRoot, pattern: "*.rf", floor: ticks);
                    ticks = NewestMtimeTicks(root: stdlibRoot, pattern: "*.sf", floor: ticks);
                }
            }
            catch
            {
                /* best-effort: fall back to the DLL-mtime-only stamp */
            }

            return ticks.ToString();
        }

        /// <summary>The newest last-write time (ticks) among files matching <paramref name="pattern"/> under
        /// <paramref name="root"/>, or <paramref name="floor"/> if none is newer. Used to fold the stdlib tree
        /// into the daemon freshness stamp.</summary>
        private static long NewestMtimeTicks(string root, string pattern, long floor)
        {
            long max = floor;
            foreach (string f in Directory.EnumerateFiles(path: root,
                         searchPattern: pattern,
                         searchOption: SearchOption.AllDirectories))
            {
                long t = File.GetLastWriteTimeUtc(path: f).Ticks;
                if (t > max)
                {
                    max = t;
                }
            }

            return max;
        }

        /// <summary>Pings the daemon and returns the compiler stamp it reported (from <c>pong &lt;stamp&gt;</c>),
        /// or null if no daemon answered.</summary>
        private static string? PingStamp(int timeoutMs)
        {
            try
            {
                DaemonResponse? resp = SendRequest(request: new DaemonRequest { Verb = "ping" },
                    timeoutMs: timeoutMs);
                if (resp?.Output is not { } output)
                {
                    return null;
                }

                int sp = output.IndexOf(value: ' ');
                return sp >= 0
                    ? output[(sp + 1)..]
                    : ""; // "pong <stamp>" → stamp; bare "pong" → "" (old daemon)
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Cheap liveness+freshness probe: a daemon counts as usable only if it answers AND is running
        /// the CURRENT compiler DLL (stamp matches). A STALE daemon (rebuilt compiler) returns false so the
        /// caller restarts it — never reused (that was the recurring GMCE / stale-snapshot bug). Never spawns.</summary>
        private static bool TryPing(int timeoutMs)
        {
            string? stamp = PingStamp(timeoutMs: timeoutMs);
            return stamp != null && stamp == CompilerVersionStamp();
        }

        /// <summary>If a daemon is up but running a STALE compiler (its stamp differs from the current DLL, or
        /// it is an old daemon that predates the version-stamped pong), send it <c>shutdown</c> and wait for the
        /// pipe to free, so a fresh daemon can take over. No-op when no daemon or a fresh one is up.</summary>
        private static void ShutdownStaleDaemonIfPresent()
        {
            string? stamp = PingStamp(timeoutMs: 300);
            if (stamp == null || stamp == CompilerVersionStamp())
            {
                return; // no daemon, or already fresh
            }

            Console.Error.WriteLine(
                value: "[daemon] running daemon is stale (compiler or stdlib changed) — restarting it.");
            try { SendRequest(request: new DaemonRequest { Verb = "shutdown" }, timeoutMs: 2000); }
            catch
            {
                /* best-effort */
            }

            // Poll until the stale daemon stops answering (pipe freed), up to ~5 s.
            for (int i = 0; i < 50 && PingStamp(timeoutMs: 100) != null; i++)
            {
                Thread.Sleep(millisecondsTimeout: 100);
            }
        }

        /// <summary>
        /// Ensures a warm daemon is reachable, spawning one if needed. <paramref name="warmWaitMs"/> is the
        /// time THIS call spent polling a freshly-spawned daemon until it warmed (0 when an already-running
        /// daemon was reused); <paramref name="spawned"/> is true only when this call started the daemon.
        /// Callers start their transfer/round-trip clock AFTER this returns so the one-time spawn+warm wait
        /// is reported on its own line instead of being mis-attributed to IPC transfer.
        /// </summary>
        private static bool EnsureDaemonRunning(out long warmWaitMs, out bool spawned)
        {
            warmWaitMs = 0;
            spawned = false;

            // (1) Already up? Reuse it — never start a second daemon.
            if (TryPing(timeoutMs: 300))
            {
                return true;
            }

            // (2) Serialize concurrent first-invocations so only one client spawns the daemon.
            using var mutex = new Mutex(initiallyOwned: false,
                name: $"Global\\razorforge-daemon-spawn-{Environment.UserName}");
            bool held;
            try
            {
                held = mutex.WaitOne(timeout: TimeSpan.FromSeconds(value: 60));
            }
            catch (AbandonedMutexException)
            {
                held = true; // a previous holder crashed; the lock is ours.
            }

            if (!held)
            {
                return TryPing(timeoutMs: 300);
            }

            try
            {
                // Another client may have spawned it while we waited on the mutex.
                if (TryPing(timeoutMs: 300))
                {
                    return true;
                }

                // A STALE daemon (older compiler DLL) may still be holding the pipe — shut it down first so
                // the fresh daemon we spawn below can bind. (TryPing above already returned false for it.)
                ShutdownStaleDaemonIfPresent();

                System.Diagnostics.Process? proc = SpawnDaemonProcess();
                if (proc == null)
                {
                    return false;
                }

                spawned = true;
                return WaitForDaemonWarm(proc: proc, warmWaitMs: out warmWaitMs);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        /// <summary>Starts a detached daemon child, choosing the command line from how THIS client was
        /// launched. Returns null when the host/DLL paths can't be resolved or the process fails to start.</summary>
        private static System.Diagnostics.Process? SpawnDaemonProcess()
        {
            string? dll = System.Reflection.Assembly.GetEntryAssembly()
                               ?.Location;
            string? host = Environment.ProcessPath;
            if (string.IsNullOrEmpty(value: dll) || string.IsNullOrEmpty(value: host))
            {
                return null;
            }

            string logPath = Path.Combine(path1: Path.GetTempPath(),
                path2: $"razorforge-daemon-{Environment.UserName}.log");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = host, UseShellExecute = false, CreateNoWindow = true
            };
            // Command line depends on HOW this client was launched:
            //   • via the apphost `RazorForge.exe`  → ProcessPath IS RazorForge.exe → spawn `RazorForge.exe
            //     daemon` (the exe already knows its DLL; passing the DLL as arg[0] makes the child read
            //     "RazorForge.dll" as the VERB, fail immediately, and never reach the daemon loop — the bug
            //     where every buildandrun re-spawned a stillborn daemon and fell back to a cold compile).
            //   • via the muxer `dotnet RazorForge.dll` → ProcessPath is dotnet(.exe) → spawn `dotnet
            //     RazorForge.dll daemon`.
            bool hostIsApphost = Path.GetFileNameWithoutExtension(path: host)
                                     .Equals(value: Path.GetFileNameWithoutExtension(path: dll),
                                          comparisonType: StringComparison.OrdinalIgnoreCase);
            if (!hostIsApphost)
            {
                psi.ArgumentList.Add(item: dll);
            }

            psi.ArgumentList.Add(item: "daemon");
            // The spawned daemon redirects its own console to this file (see RunServer), so it is fully
            // detached from this short-lived client's console and survives the client exiting.
            psi.Environment[key: DaemonLogEnvVar] = logPath;

            var proc = System.Diagnostics.Process.Start(startInfo: psi);
            if (proc == null)
            {
                return null;
            }

            Console.Error.WriteLine(
                value:
                $"[daemon] no daemon running — started one (pid {proc.Id}, log: {logPath}); warming…");
            return proc;
        }

        /// <summary>Polls a freshly-spawned daemon until it answers a ping (warmed) or the ~60 s budget
        /// elapses / the child exits. On success sets <paramref name="warmWaitMs"/> to the wait duration.</summary>
        private static bool WaitForDaemonWarm(System.Diagnostics.Process proc, out long warmWaitMs)
        {
            warmWaitMs = 0;
            // (3) Wait for warm-up (stdlib snapshot capture ≈ 5 s). Ping is process-free, so this poll
            // does not starve the warming daemon.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(value: 60))
            {
                if (proc.HasExited)
                {
                    return false;
                }

                if (TryPing(timeoutMs: 300))
                {
                    warmWaitMs = sw.ElapsedMilliseconds;
                    return true;
                }

                Thread.Sleep(millisecondsTimeout: 150);
            }

            return false;
        }

        // ---- length-prefixed JSON framing ----------------------------------------------------------

        private static void WriteMessage<T>(Stream stream, T value)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value: value);
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination: len,
                value: payload.Length);
            stream.Write(buffer: len);
            stream.Write(buffer: payload, offset: 0, count: payload.Length);
            stream.Flush();
        }

        private static T? ReadMessage<T>(Stream stream) where T : class
        {
            byte[] lenBuf = new byte[4];
            if (!ReadExact(stream: stream, buffer: lenBuf, count: 4))
            {
                return null;
            }

            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source: lenBuf);
            if (len <= 0 || len > 64 * 1024 * 1024)
            {
                return null;
            }

            byte[] payload = new byte[len];
            if (!ReadExact(stream: stream, buffer: payload, count: len))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(utf8Json: payload);
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer: buffer, offset: read, count: count - read);
                if (n <= 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }

    }
}
