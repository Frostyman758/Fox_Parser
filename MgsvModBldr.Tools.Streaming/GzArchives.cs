// Index of GZ data_NN.g0s archives
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Index of GZ's content archives (data_00/01/02.g0s, sitting next to
/// MgsGroundZeroes.exe) — the G0s analogue of GameArchives. Maps each entry hash to
/// every archive that holds it, so a mod file can be overridden in EVERY instance,
/// exactly like the TPP engine. Reads the live archives directly (no baselines;
/// restore = Steam "verify integrity").
/// </summary>
public sealed class GzArchives
{
    public static readonly string[] BaseNames = { "data_00.g0s", "data_01.g0s", "data_02.g0s" };

    public sealed class Home
    {
        public string Path = "";
        public GzReader Reader = null!;
        public G0sEntry Entry = null!;
    }

    private readonly Dictionary<ulong, List<Home>> _byHash = new();
    private readonly Dictionary<string, GzReader> _readers = new();

    public IReadOnlyList<Home> FindAll(ulong hash) =>
        _byHash.TryGetValue(hash, out var l) ? l : (IReadOnlyList<Home>)Array.Empty<Home>();

    public GzReader Reader(string name) => _readers.TryGetValue(name, out var r) ? r : null;

    public static GzArchives Build(string gzDir)
    {
        var ga = new GzArchives();
        var sources = new List<(string name, string path)>();
        foreach (var name in BaseNames)
        {
            string p = Path.Combine(gzDir, name);
            // data_00.g0s is a .wmv movie, not an archive — magic, not extension.
            if (File.Exists(p) && ArchiveFormat.Detect(p) == FoxArchiveKind.G0s) sources.Add((name, p));
        }

        var readers = new GzReader[sources.Count];
        Parallel.For(0, sources.Count, i => readers[i] = new GzReader(sources[i].path));

        for (int i = 0; i < sources.Count; i++)
        {
            var rd = readers[i];
            ga._readers[sources[i].name] = rd;
            foreach (var e in rd.Entries)
            {
                if (!ga._byHash.TryGetValue(e.Hash, out var list)) { list = new(); ga._byHash[e.Hash] = list; }
                list.Add(new Home { Path = rd.Path, Reader = rd, Entry = e });
            }
        }
        return ga;
    }
}
