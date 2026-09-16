using System.Security.Cryptography;
using System.Text;

namespace Builder.Execution;

/// <summary>
/// Resident-JIT incremental (B) M2b: a disk-backed per-routine IR cache for the fully-lazy on-demand JIT
/// (<see cref="OrcJitExecutor.JitAndRunLazy"/>). Keyed by (stdlib+compiler fingerprint, mangled routine name),
/// so a cached one-routine IR module is reused across runs/edits as long as the stdlib and compiler are
/// unchanged — turning M2a's per-run re-emit of every reached routine into a FIRST-RUN-ONLY cost.
///
/// CORRECTNESS: only PURE-STDLIB routines are cached. A routine whose mangled name references a user module
/// (a monomorphized instance over a user type, e.g. <c>List[Main.UserType].add_last</c>) depends on user code
/// that changes between edits, so its IR is NOT invariant under the fingerprint — those are never cached and
/// are re-codegen'd each run (a small set). The fingerprint (stdlib source + compiler asm mtime, via
/// <see cref="Serialization.StdlibSnapshotCache.ComputeStdlibHash"/>) invalidates every entry when the stdlib
/// or compiler changes. This is the §2A.2② on-demand monomorphization cache, scoped to the safe subset.
/// </summary>
public sealed class RoutineIrCache
{
    private readonly string _dir;
    private readonly string _fingerprint;
    private readonly IReadOnlyList<string> _userModuleSegments;

    /// <summary>Cached one-routine modules reused from disk this session (no codegen).</summary>
    public int Hits { get; private set; }

    /// <summary>Cacheable routines codegen'd + written this session (first-run cost).</summary>
    public int Misses { get; private set; }

    /// <summary>User-dependent routines that are never cached (re-codegen'd each run).</summary>
    public int Uncacheable { get; private set; }

    /// <param name="fingerprint">Stdlib+compiler content hash; every entry is keyed under it, so a stdlib or
    /// compiler change invalidates the whole cache.</param>
    /// <param name="userModuleSegments">Module-name segments that mark a routine as user-dependent (its IR is
    /// NOT cached). Typically the user program's module name(s).</param>
    /// <param name="dir">Cache directory; defaults to a per-fingerprint temp subdir.</param>
    public RoutineIrCache(string fingerprint, IReadOnlyList<string> userModuleSegments,
        string? dir = null)
    {
        _fingerprint = fingerprint;
        _userModuleSegments = userModuleSegments;
        _dir = dir ?? Path.Combine(path1: Path.GetTempPath(), path2: "rf_jit_ir_cache");
        Directory.CreateDirectory(path: _dir);
    }

    private bool IsCacheable(string mangledName)
    {
        foreach (string seg in _userModuleSegments)
        {
            if (seg.Length > 0 &&
                mangledName.Contains(value: seg, comparisonType: StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private string PathFor(string mangledName)
    {
        byte[] h = SHA256.HashData(
            source: Encoding.UTF8.GetBytes(s: _fingerprint + "\0" + mangledName));
        return Path.Combine(path1: _dir, path2: Convert.ToHexString(inArray: h) + ".ll");
    }

    /// <summary>Returns a cached one-routine IR module for <paramref name="mangledName"/>, or false (miss /
    /// uncacheable). A hit skips codegen entirely.</summary>
    public bool TryGet(string mangledName, out string ir)
    {
        ir = "";
        if (!IsCacheable(mangledName: mangledName))
        {
            return false;
        }

        string p = PathFor(mangledName: mangledName);
        if (!File.Exists(path: p))
        {
            return false;
        }

        ir = File.ReadAllText(path: p);
        Hits++;
        return true;
    }

    /// <summary>Stores a freshly-codegen'd one-routine IR module (only for cacheable pure-stdlib routines).</summary>
    public void Put(string mangledName, string ir)
    {
        if (!IsCacheable(mangledName: mangledName))
        {
            Uncacheable++;
            return;
        }

        File.WriteAllText(path: PathFor(mangledName: mangledName), contents: ir);
        Misses++;
    }
}
