// Index of the game archives by hash
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Index of the game's archives. A file can exist in several archives at once
/// (e.g. init.lua lives in data1 AND the patch archives 00/01), and the game reads
/// the highest-priority copy — the patch archives override the base ones. So to make
/// a mod's file actually take effect we must override EVERY instance of it.
///
/// Indexed set = base archives (data1, chunk0-6, texture0-6) PLUS every patch
/// archive (any 00.dat/01.dat under master\, incl. title updates). The live files
/// are read directly — no baselines (restore = Steam "verify integrity"). New files
/// are only ever ADDED to a base archive, never to a patch archive (that won't boot).
/// </summary>
public sealed class GameArchives
{
    // Base archives — also the only valid targets for brand-new files.
    public static readonly string[] BaseNames =
    {
        "data1.dat",
        "chunk0.dat", "chunk1.dat", "chunk2.dat", "chunk3.dat", "chunk4.dat",
        "chunk5_mgo0.dat", "chunk6_gzs0.dat",
        "texture0.dat", "texture1.dat", "texture2.dat", "texture3.dat", "texture4.dat",
        "texture5_mgo0.dat", "texture6_gzs0.dat",
    };

    public sealed class Home
    {
        public string Path = "";       // full path of the archive holding this entry
        public QarReader Reader = null!;
        public QarEntry Entry = null!;
    }

    private readonly Dictionary<ulong, List<Home>> _byHash = new();
    private readonly Dictionary<string, QarReader> _baseByName = new(); // base archives, by file name

    /// <summary>Every archive that holds this file's hash (across base + patch archives).</summary>
    public IReadOnlyList<Home> FindAll(ulong hash) =>
        _byHash.TryGetValue(hash, out var l) ? l : (IReadOnlyList<Home>)Array.Empty<Home>();

    public bool Contains(ulong hash) => _byHash.ContainsKey(hash);

    /// <summary>A base archive reader by file name (e.g. "chunk0.dat") — for routing new files.</summary>
    public QarReader BaseReader(string name) => _baseByName.TryGetValue(name, out var r) ? r : null;

    public static GameArchives Build(string gameDir)
    {
        var ga = new GameArchives();
        string master = Path.Combine(gameDir, "master");

        // base archives (top-level) + every patch archive (00.dat/01.dat anywhere under master\)
        var baseSources = new List<(string name, string path)>();
        foreach (var name in BaseNames)
        {
            string p = Path.Combine(master, name);
            if (File.Exists(p)) baseSources.Add((name, p));
        }

        var patchPaths = Directory.Exists(master)
            ? Directory.EnumerateFiles(master, "*.dat", SearchOption.AllDirectories)
                .Where(p => { var n = Path.GetFileName(p); return n.Equals("00.dat", StringComparison.OrdinalIgnoreCase) || n.Equals("01.dat", StringComparison.OrdinalIgnoreCase); })
                .ToList()
            : new List<string>();

        var allPaths = baseSources.Select(s => s.path).Concat(patchPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // open + index in parallel (header-only reads of the live archives)
        var readers = new QarReader[allPaths.Length];
        Parallel.For(0, allPaths.Length, i => readers[i] = new QarReader(allPaths[i]));

        var byName = baseSources.ToDictionary(s => s.path, s => s.name, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allPaths.Length; i++)
        {
            var rd = readers[i];
            if (byName.TryGetValue(allPaths[i], out var bn)) ga._baseByName[bn] = rd;
            foreach (var e in rd.Entries)
            {
                ulong h = e.Header.PathHash;
                if (!ga._byHash.TryGetValue(h, out var list)) { list = new(); ga._byHash[h] = list; }
                list.Add(new Home { Path = rd.Path, Reader = rd, Entry = e });
            }
        }
        return ga;
    }
}
