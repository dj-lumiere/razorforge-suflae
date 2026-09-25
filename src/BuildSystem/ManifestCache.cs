namespace Builder;

/// <summary>
/// Caches the parsed <c>config.toml</c> of an explicit-entry build (<c>buildandrun file.rf</c>) as a small binary
/// file, so a dev-loop run does not load and JIT the TOML parser (its first use costs ~20 ms per process). The key
/// covers the manifest's full path, its exact text, and the compiler DLL's timestamp, so any edit to the manifest
/// or a rebuilt compiler misses the cache. Only manifests whose parse depends on nothing but their own text are
/// cached: a <c>library</c> entry is checked against the disk and <c>[libraries.X]</c> tables are rich, so a
/// manifest with either is always parsed.
/// </summary>
internal static class ManifestCache
{
    /// <summary>Bumped whenever the cached field sequence changes.</summary>
    private const int FormatVersion = 1;

    /// <summary>Reads the cached manifest for this path and text, or returns false.</summary>
    public static bool TryRead(string fullPath, string content, out ProjectManifest? manifest)
    {
        manifest = null;
        try
        {
            string file = CacheFile(fullPath: fullPath, content: content);
            if (!File.Exists(path: file))
            {
                return false;
            }

            using var reader = new BinaryReader(input: File.OpenRead(path: file),
                encoding: System.Text.Encoding.UTF8);
            if (reader.ReadInt32() != FormatVersion)
            {
                return false;
            }

            var loaded = new ProjectManifest
            {
                ManifestDirectory = Path.GetDirectoryName(path: fullPath)!
            };
            loaded.Package.Name = reader.ReadString();
            loaded.Target.Executable = reader.ReadString();
            loaded.Target.Mode = reader.ReadString();
            loaded.Target.UseDaemon = reader.ReadBoolean();
            loaded.Target.Incremental = reader.ReadBoolean();
            loaded.Target.CLibraries = ReadList(reader: reader);
            loaded.Target.LibraryPaths = ReadList(reader: reader);
            loaded.Debug.DumpAst = reader.ReadBoolean();
            loaded.Debug.Timing = reader.ReadBoolean();
            loaded.Debug.ShowBuildStages = reader.ReadBoolean();
            loaded.Debug.PruneStats = reader.ReadBoolean();
            loaded.Debug.JitTrace = reader.ReadBoolean();
            loaded.Debug.DumpIr = reader.ReadBoolean();
            loaded.Debug.ReachabilityDump = ReadNullable(reader: reader);
            loaded.Debug.MaySuspendDump = ReadNullable(reader: reader);
            manifest = loaded;
            return true;
        }
        catch (Exception)
        {
            // A torn or unreadable cache file is just a miss: the caller parses the manifest.
            return false;
        }
    }

    /// <summary>Stores a freshly parsed manifest when its parse depends only on its own text.</summary>
    public static void TryWrite(string fullPath, string content, ProjectManifest manifest)
    {
        if (manifest.Target.Libraries.Count > 0 || manifest.Target.LibraryConfigs.Count > 0)
        {
            return;
        }

        try
        {
            string file = CacheFile(fullPath: fullPath, content: content);
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: file)!);

            // Write beside the target and move into place, so a concurrent reader never sees half a file.
            string temp = $"{file}.{Environment.ProcessId}.tmp";
            using (var writer = new BinaryWriter(output: File.Create(path: temp),
                       encoding: System.Text.Encoding.UTF8))
            {
                writer.Write(value: FormatVersion);
                writer.Write(value: manifest.Package.Name);
                writer.Write(value: manifest.Target.Executable);
                writer.Write(value: manifest.Target.Mode);
                writer.Write(value: manifest.Target.UseDaemon);
                writer.Write(value: manifest.Target.Incremental);
                WriteList(writer: writer, values: manifest.Target.CLibraries);
                WriteList(writer: writer, values: manifest.Target.LibraryPaths);
                writer.Write(value: manifest.Debug.DumpAst);
                writer.Write(value: manifest.Debug.Timing);
                writer.Write(value: manifest.Debug.ShowBuildStages);
                writer.Write(value: manifest.Debug.PruneStats);
                writer.Write(value: manifest.Debug.JitTrace);
                writer.Write(value: manifest.Debug.DumpIr);
                WriteNullable(writer: writer, value: manifest.Debug.ReachabilityDump);
                WriteNullable(writer: writer, value: manifest.Debug.MaySuspendDump);
            }

            File.Move(sourceFileName: temp, destFileName: file, overwrite: true);
        }
        catch (Exception)
        {
            // Best-effort: without a cache entry the next run parses the manifest again.
        }
    }

    /// <summary>The cache file for this manifest path, text, and compiler build.</summary>
    private static string CacheFile(string fullPath, string content)
    {
        string? compiler = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        long compilerTicks = !string.IsNullOrEmpty(value: compiler) && File.Exists(path: compiler)
            ? File.GetLastWriteTimeUtc(path: compiler).Ticks
            : 0;
        ulong hash = Fnv1a(text: $"{FormatVersion}\0{compilerTicks}\0{fullPath}\0{content}");
        return Path.Combine(path1: Path.GetTempPath(),
            path2: "razorforge",
            path3: "manifest-cache",
            path4: $"{hash:x16}.bin");
    }

    /// <summary>64-bit FNV-1a over the text's UTF-16 code units: a cheap, stable content key.</summary>
    private static ulong Fnv1a(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value: value != null);
        if (value != null)
        {
            writer.Write(value: value);
        }
    }

    private static string? ReadNullable(BinaryReader reader)
    {
        return reader.ReadBoolean()
            ? reader.ReadString()
            : null;
    }

    private static void WriteList(BinaryWriter writer, List<string> values)
    {
        writer.Write(value: values.Count);
        foreach (string value in values)
        {
            writer.Write(value: value);
        }
    }

    private static List<string> ReadList(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new List<string>(capacity: count);
        for (int i = 0; i < count; i++)
        {
            values.Add(item: reader.ReadString());
        }

        return values;
    }
}
