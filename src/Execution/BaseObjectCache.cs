using System.Security.Cryptography;
using System.Text;
using Builder.Targeting;

namespace Builder.Execution;

/// <summary>
/// Daemon-lifetime disk cache of the AOT-compiled resident-JIT BASE object file. The stdlib closure IR is
/// invariant under the stdlib+compiler fingerprint, so it is compiled to a native <c>.o</c> ONCE per
/// fingerprint (opt + <c>clang -c</c>); every subsequent dev-loop build LOADS that object into the JIT and
/// codegen/JITs only the per-run DELTA (user code + non-resident instantiations), instead of re-JIT-compiling
/// ~1 MB of stdlib IR each run. Keyed purely by the fingerprint — a stdlib or compiler change yields a new key
/// (new file), so a stale base can never be loaded. The base IR itself comes from
/// <c>LlvmEmitter.GenerateBase</c> (non-pruned stdlib, no <c>@main</c>); the per-run delta extern-declares the
/// symbols this object defines and the JIT resolves delta→base across the link (see OrcJitExecutor).
/// </summary>
public sealed class BaseObjectCache
{
    /// <summary>
    /// The REALISTIC-SEED base program. The resident-JIT base is NOT built from
    /// <c>SeedAllStdlibRoutines</c> (which speculatively materializes every generic instance — measured 4268
    /// bodies, 53% of them <c>Hijacked[…]</c> cycle-collector combinations no real program uses — and bloats
    /// the base). Under monomorphization there is no separable "non-generic stdlib core" to precompile (an
    /// empty-<c>start</c> analysis yields only ~38 per-type lifecycle hooks). The stable, amortizable surface
    /// is instead the COMMON generic instances that most programs share — List/Dict/Set/Maybe over common
    /// element types plus the scalar/collection display closure — so we build the base by analyzing a
    /// representative program that actually EXERCISES them. The normal demand pipeline then materializes
    /// exactly that reached closure; each program's own tail (uncommon instances) falls to the per-run delta.
    /// This constant participates in the base fingerprint, so editing the seed rebuilds the base.
    /// </summary>
    public const string SeedProgramSource =
        "module Base\n" +
        "import IO/Console\n" +
        "routine start()\n" +
        "  var xs = [1, 2, 3]\n" +
        "  xs.add_last(value: 4)\n" +
        "  xs.add_first(value: 0)\n" +
        "  show(f\"xs: {xs} size {xs.count()} first {xs[0]}\")\n" +
        "  var sum = 0\n" +
        "  each x in xs\n" +
        "    sum = sum + x\n" +
        "  show(f\"sum: {sum}\")\n" +
        "  var names = [\"alpha\", \"beta\"]\n" +
        "  names.add_last(value: \"gamma\")\n" +
        "  show(f\"names: {names} have beta {names have \"beta\"}\")\n" +
        "  var d = Dict[Text, S64]()\n" +
        "  discard d.add(key: \"one\", value: 1)\n" +
        "  discard d.add(key: \"two\", value: 2)\n" +
        "  show(f\"d: {d} d[one] {d[\"one\"]} have two {d have \"two\"}\")\n" +
        "  var di = Dict[S64, S64]()\n" +
        "  discard di.add(key: 10, value: 100)\n" +
        "  show(f\"di: {di} size {di.count()}\")\n" +
        "  var s = Set[S64]()\n" +
        "  discard s.add(value: 7)\n" +
        "  discard s.add(value: 8)\n" +
        "  show(f\"s: {s} size {s.count()} have 7 {s have 7}\")\n" +
        "  var b = true\n" +
        "  var n: S64 = 42\n" +
        "  var u: U64 = 42\n" +
        "  show(f\"scalars: {b} {n} {u} {n + 1} {n // 2}\")\n" +
        "  show(\"BASE_SEED_DONE\")\n" +
        "  return\n";

    private readonly string _dir;

    public BaseObjectCache(string? dir = null)
    {
        _dir = dir ?? Path.Combine(path1: Path.GetTempPath(), path2: "rf_jit_base_cache");
        Directory.CreateDirectory(path: _dir);
    }

    /// <summary>The cached object path for a fingerprint (the file may not exist yet).</summary>
    public string ObjectPath(string fingerprint)
    {
        byte[] h = SHA256.HashData(source: Encoding.UTF8.GetBytes(s: fingerprint));
        return Path.Combine(path1: _dir, path2: Convert.ToHexString(inArray: h) + ".o");
    }

    /// <summary>True when the base object for this fingerprint is already on disk (a hit — the fast path).</summary>
    public bool Has(string fingerprint)
    {
        return File.Exists(path: ObjectPath(fingerprint: fingerprint));
    }

    /// <summary>
    /// Returns the cached base object path, compiling it from <paramref name="baseIr"/> on a miss
    /// (opt → <c>clang -c</c>). Returns null if the AOT compile fails. Sets <paramref name="wasCached"/> to
    /// whether the object already existed — a hit skips all compilation.
    /// </summary>
    public string? GetOrBuild(string fingerprint, string baseIr, RfBuildMode buildMode,
        out bool wasCached)
    {
        string objPath = ObjectPath(fingerprint: fingerprint);
        if (File.Exists(path: objPath))
        {
            wasCached = true;
            return objPath;
        }

        wasCached = false;
        // Fingerprint-scoped intermediates so distinct fingerprints never collide on disk.
        string stem = Path.GetFileNameWithoutExtension(path: objPath);
        string llPath = Path.Combine(path1: _dir, path2: stem + ".ll");
        string optPath = Path.Combine(path1: _dir, path2: stem + ".opt.ll");
        File.WriteAllText(path: llPath, contents: baseIr);

        if (Builder.NativeToolchain.OptimizeIr(llFile: llPath, optFile: optPath,
                buildMode: buildMode) != 0)
        {
            return null;
        }

        // Compile to a TEMP object then atomically move into place, so a crashed/partial compile never
        // leaves a corrupt .o that a later run would load as a valid cache hit.
        string tmpObj = objPath + ".tmp";
        if (Builder.NativeToolchain.CompileIrToObject(optFile: optPath, objFile: tmpObj,
                buildMode: buildMode) != 0)
        {
            return null;
        }

        File.Move(sourceFileName: tmpObj, destFileName: objPath, overwrite: true);
        return objPath;
    }
}
