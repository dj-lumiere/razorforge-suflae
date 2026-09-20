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
