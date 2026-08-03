// Stream one file out of a game install
namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Stream a single file out of a whole game install without unpacking anything.
/// Indexes every archive's header once (TPP: master\*.dat incl. patch archives;
/// GZ: data_NN.g0s), then decodes just the requested entry.
///
/// A path can live in several archives at once; the patch archives are indexed
/// after the base ones, so the LAST home is the copy the game actually loads.
/// </summary>
public sealed class GameStream
{
    private readonly GameArchives _tpp;
    private readonly GzArchives _gz;

    public bool IsGz => _gz is not null;

    private GameStream(GameArchives tpp, GzArchives gz) { _tpp = tpp; _gz = gz; }

    /// <summary>Open a TPP install (folder holding master\) or a GZ install (folder holding data_NN.g0s).</summary>
    public static GameStream Open(string gameDir)
    {
        bool gz = false;
        foreach (var n in GzArchives.BaseNames)
            if (File.Exists(Path.Combine(gameDir, n))) { gz = true; break; }
        return gz ? new GameStream(null, GzArchives.Build(gameDir))
                  : new GameStream(GameArchives.Build(gameDir), null);
    }

    /// <summary>Archives holding this path, lowest priority first (empty if absent).</summary>
    public IReadOnlyList<string> Homes(string gamePath)
    {
        var l = new List<string>();
        if (_gz is not null)
            foreach (var h in _gz.FindAll(GzHashing.NameToHash(gamePath))) l.Add(h.Path);
        else
            foreach (var h in _tpp.FindAll(Hashing.NameToHash(gamePath))) l.Add(h.Path);
        return l;
    }

    /// <summary>Decoded bytes of the copy the game loads (the highest-priority home).</summary>
    public byte[] Read(string gamePath)
    {
        if (_gz is not null)
        {
            var homes = _gz.FindAll(GzHashing.NameToHash(gamePath));
            if (homes.Count == 0) throw new FileNotFoundException($"{gamePath} is not in this GZ install");
            var h = homes[homes.Count - 1];
            return h.Reader.ReadDecoded(h.Entry);
        }
        var t = _tpp.FindAll(Hashing.NameToHash(gamePath));
        if (t.Count == 0) throw new FileNotFoundException($"{gamePath} is not in this install");
        var w = t[t.Count - 1];
        return w.Reader.ReadDecoded(w.Entry);
    }

    /// <summary>Write that copy straight to a file. Returns the byte count written.</summary>
    public long Extract(string gamePath, string outFile)
    {
        var bytes = Read(gamePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outFile, bytes);
        return bytes.LongLength;
    }
}
