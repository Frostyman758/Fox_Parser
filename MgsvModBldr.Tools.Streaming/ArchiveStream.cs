// Stream one entry out of an archive
using MgsvModBldr.Tools.Fpk;
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Pull a SINGLE file out of a .dat/.qar/.g0s without unpacking the archive.
/// Only the index is parsed; one entry's block is read and decoded. An entry that
/// lives inside a nested .fpk/.fpkd is reached with a path that walks through the
/// pack ("Assets/.../foo.fpk/Assets/.../inner.fox2") — the pack alone is decoded,
/// nothing else is touched.
/// </summary>
public static class ArchiveStream
{
    public sealed record Item(string Path, ulong Hash, long Size, bool Compressed);

    // Extensions lie (master\e2f*.dat and GZ's data_00.g0s are .wmv movies), so
    // every entry point asks the magic. Throws a plain message for the rest.
    private static FoxArchiveKind KindOf(string path)
    {
        var k = ArchiveFormat.Detect(path);
        if (!ArchiveFormat.IsArchive(k)) throw new InvalidDataException(ArchiveFormat.Describe(path, k));
        return k;
    }

    public static bool IsQar(string p) => ArchiveFormat.Detect(p) == FoxArchiveKind.Qar;

    public static bool IsG0s(string p) => ArchiveFormat.Detect(p) == FoxArchiveKind.G0s;

    /// <summary>
    /// Index-only listing: no entry body is read. Entries carry a hash, not a name,
    /// so <paramref name="resolveNames"/> runs each one through the name dictionary
    /// (Path stays null for the ones it can't name).
    /// </summary>
    public static IReadOnlyList<Item> List(string archivePath, bool resolveNames = true)
    {
        if (KindOf(archivePath) == FoxArchiveKind.G0s)
        {
            var gr = new GzReader(archivePath);
            var l = new List<Item>(gr.Entries.Count);
            foreach (var e in gr.Entries)
                l.Add(new Item(resolveNames && G0sHash.TryResolve(e.Hash, out var gp) ? gp : null, e.Hash, e.Size, false));
            return l;
        }
        var qr = new QarReader(archivePath);
        var dict = resolveNames ? QarDictionary.Load() : null;
        var q = new List<Item>(qr.Entries.Count);
        foreach (var e in qr.Entries)
        {
            var name = e.Header.FilePath;
            if (string.IsNullOrEmpty(name) && dict is not null)
            {
                var r = dict.Resolve(e.Header.PathHash, out bool found);
                if (found) name = r;
            }
            q.Add(new Item(string.IsNullOrEmpty(name) ? null : name, e.Header.PathHash,
                           (long)e.Header.UncompressedSize, e.Header.Compressed));
        }
        return q;
    }

    /// <summary>Decoded bytes of one entry, addressed by its archive hash.</summary>
    public static byte[] Read(string archivePath, ulong hash)
    {
        if (KindOf(archivePath) == FoxArchiveKind.G0s)
        {
            var gr = new GzReader(archivePath);
            var ge = gr.Find(hash) ?? throw new FileNotFoundException($"hash {hash:x16} not in {Path.GetFileName(archivePath)}");
            return gr.ReadDecoded(ge);
        }
        var qr = new QarReader(archivePath);
        var e = qr.Find(hash) ?? throw new FileNotFoundException($"hash {hash:x16} not in {Path.GetFileName(archivePath)}");
        return qr.ReadDecoded(e);
    }

    /// <summary>
    /// Decoded bytes of one entry, addressed by game path. The path may continue
    /// through a nested .fpk/.fpkd (one level).
    /// </summary>
    public static byte[] Read(string archivePath, string entryPath)
    {
        var (outer, interior) = SplitAtPack(entryPath);
        var bytes = ReadTop(archivePath, outer);
        return interior is null ? bytes : FromPack(bytes, interior);
    }

    /// <summary>Write one entry straight to a file. Returns the byte count written.</summary>
    public static long Extract(string archivePath, string entryPath, string outFile)
    {
        var bytes = Read(archivePath, entryPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outFile, bytes);
        return bytes.LongLength;
    }

    private static byte[] ReadTop(string archivePath, string entryPath)
    {
        if (KindOf(archivePath) == FoxArchiveKind.G0s)
        {
            var gr = new GzReader(archivePath);
            var ge = gr.Find(entryPath) ?? throw new FileNotFoundException($"{entryPath} not in {Path.GetFileName(archivePath)}");
            return gr.ReadDecoded(ge);
        }
        var qr = new QarReader(archivePath);
        var e = qr.Find(entryPath) ?? throw new FileNotFoundException($"{entryPath} not in {Path.GetFileName(archivePath)}");
        return qr.ReadDecoded(e);
    }

    /// <summary>Pull one inner file out of an in-memory fpk/fpkd (TPP or GZ).</summary>
    public static byte[] FromPack(byte[] packBytes, string interior)
    {
        string want = Norm(interior);

        // GZ packs carry the "ste" platform tag and a different reader; the TPP
        // FpkFile rejects them outright ("unknown fpk(d) magic").
        if (MgsvModBldr.Tools.Fpk.Gz.GzFpkFile.IsGzMagic(packBytes.AsSpan(0, Math.Min(packBytes.Length, 10))))
        {
            using var gms = new MemoryStream(packBytes, writable: false);
            var gz = MgsvModBldr.Tools.Fpk.Gz.GzFpkFile.Read(gms);
            foreach (var e in gz.Entries)
            {
                var have = Norm(e.FilePath);
                if (have == want || have.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase)) return e.Data;
            }
            throw new FileNotFoundException($"'{interior}' not inside the GZ pack");
        }

        var fpk = new FpkFile();
        using (var ms = new MemoryStream(packBytes, writable: false)) fpk.Read(ms);
        foreach (var e in fpk.Entries)
        {
            var have = Norm(e.FilePath.Data);
            if (have == want || have.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase)) return e.Data;
        }
        throw new FileNotFoundException($"'{interior}' not inside the pack");
    }

    // "a/b/foo.fpk/c/d.fox2" -> ("a/b/foo.fpk", "c/d.fox2"); no pack segment -> (path, null)
    private static (string outer, string interior) SplitAtPack(string entryPath)
    {
        var p = entryPath.Replace('\\', '/');
        int at = 0;
        while (true)
        {
            int slash = p.IndexOf('/', at);
            if (slash < 0) return (p, null);
            var seg = p[..slash];
            if (seg.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) ||
                seg.EndsWith(".fpkd", StringComparison.OrdinalIgnoreCase))
                return (seg, p[(slash + 1)..]);
            at = slash + 1;
        }
    }

    private static string Norm(string s) => s is null ? "" : s.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}
