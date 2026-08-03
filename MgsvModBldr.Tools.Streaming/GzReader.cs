// Lazy .g0s index + blob reader
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Lazy .g0s index + block reader — the G0s analogue of QarReader. Reads only the
/// footer + entry table (via G0sArchive.ReadIndex), then pulls a single entry's raw
/// on-disk blob or its decrypted plaintext on demand.
///
/// Unlike a TPP QAR block, a .g0s blob is OFFSET-KEYED (its encryption depends on the
/// entry's byte offset), so a blob can only be copied verbatim if it stays at the same
/// offset — which is exactly what the GzWriter splice preserves for unchanged entries.
/// </summary>
public sealed class GzReader
{
    public string Path { get; }
    public IReadOnlyList<G0sEntry> Entries => _arc.Entries;

    /// <summary>Byte offset where the entry table starts (= end of the data region, 16-aligned).</summary>
    public long DataRegionEnd { get; }

    private readonly G0sArchive _arc;

    public GzReader(string path)
    {
        Path = path;
        using var fs = File.OpenRead(path);
        _arc = G0sArchive.ReadIndex(fs);

        long max = 0;
        foreach (var e in _arc.Entries)
        {
            long end = 16L * e.Offset + e.Size;
            if (end > max) max = end;
        }
        DataRegionEnd = (max + 15) & ~15L;   // == 16 * entryBlockOffset
    }

    /// <summary>Find an entry by its archive hash.</summary>
    public G0sEntry Find(ulong hash)
    {
        foreach (var e in _arc.Entries)
            if (e.Hash == hash) return e;
        return null;
    }

    /// <summary>Find an entry by game path (hashed with the GZ scheme).</summary>
    public G0sEntry Find(string gamePath) => Find(GzHashing.NameToHash(gamePath));

    /// <summary>On-disk [offset, length) of an entry's raw blob (for verbatim splice-copy).</summary>
    public (long Offset, int Length) BlockExtent(G0sEntry e) => (16L * e.Offset, (int)e.Size);

    public byte[] ReadRawBlock(G0sEntry e)
    {
        using var fs = File.OpenRead(Path);
        fs.Seek(16L * e.Offset, SeekOrigin.Begin);
        var buf = new byte[e.Size];
        int n = 0;
        while (n < buf.Length)
        {
            int r = fs.Read(buf, n, buf.Length - n);
            if (r == 0) throw new EndOfStreamException($"short read for g0s block at {16L * e.Offset}");
            n += r;
        }
        return buf;
    }

    /// <summary>Decrypt an entry to plaintext (outer + optional inner pass) — used as a merge source.</summary>
    public byte[] ReadDecoded(G0sEntry e)
    {
        var (data, _) = G0sArchive.Decrypt(ReadRawBlock(e), e.Offset);
        return data;
    }
}
