// Offset-preserving .g0s splice writer
using System.Buffers.Binary;
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Splice-writes a .g0s. Because blobs are OFFSET-KEYED, unchanged entries can only be
/// copied verbatim if they keep their offset — so we copy the whole original data
/// region byte-for-byte (every original blob stays put), then APPEND each modded/new
/// file freshly re-encrypted at its new offset, then rebuild the entry table + footer.
///
/// An overridden entry's old blob stays in the copied region as dead space (its table
/// slot now points at the appended copy); a Steam-verify reclaims that. This mirrors the
/// QAR splice (copy unchanged + re-encode modded) within G0s's offset-keyed constraint.
/// </summary>
public static class GzWriter
{
    /// <summary>edits: hash → new plaintext (override an existing entry, or add a new one).</summary>
    public static void Write(string finalPath, GzReader live, IReadOnlyDictionary<ulong, byte[]> edits)
    {
        long dataEnd = live.DataRegionEnd;
        var liveHashes = new HashSet<ulong>();
        foreach (var e in live.Entries) liveHashes.Add(e.Hash);

        var appended = new Dictionary<ulong, (uint off, uint size)>();
        string tmp = finalPath + ".gztmp";
        var pad = new byte[16];

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            // 1. copy [0, dataEnd) verbatim — every original blob keeps its offset.
            using (var src = new FileStream(live.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buf = new byte[1 << 20];
                long remaining = dataEnd;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(remaining, buf.Length);
                    int got = src.Read(buf, 0, want);
                    if (got == 0) throw new EndOfStreamException("short copy of g0s data region");
                    fs.Write(buf, 0, got);
                    remaining -= got;
                }
            }

            // 2. append each modded/new blob, re-encrypted at its appended offset.
            long pos = dataEnd;
            foreach (var (hash, plaintext) in edits)
            {
                uint off = (uint)(pos / 16);
                byte[] blob = G0sArchive.Encrypt(plaintext, off, null);   // modded content: no inner cipher
                fs.Write(blob, 0, blob.Length);
                appended[hash] = (off, (uint)blob.Length);
                pos += blob.Length;
                long rem = pos % 16;
                if (rem != 0) { int p = (int)(16 - rem); fs.Write(pad, 0, p); pos += p; }
            }

            // 3. entry table: live order (edited entries repointed), then any brand-new entries.
            uint entryBlockOffset = (uint)(pos / 16);
            var table = new List<(ulong h, uint off, uint size)>(live.Entries.Count + edits.Count);
            foreach (var e in live.Entries)
                table.Add(appended.TryGetValue(e.Hash, out var ap) ? (e.Hash, ap.off, ap.size) : (e.Hash, e.Offset, e.Size));
            foreach (var (hash, _) in edits)
                if (!liveHashes.Contains(hash)) { var ap = appended[hash]; table.Add((hash, ap.off, ap.size)); }

            var tb = new byte[table.Count * 16];
            ulong sizeSum = 0;
            for (int i = 0; i < table.Count; i++)
            {
                var s = tb.AsSpan(i * 16, 16);
                BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0, 8), table[i].h);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), table[i].off);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12, 4), table[i].size);
                sizeSum += table[i].size;
            }
            fs.Write(tb, 0, tb.Length); pos += tb.Length;

            Span<byte> sumb = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sumb, (uint)sizeSum);
            fs.Write(sumb); pos += 4;

            long rem2 = pos % 16;
            if (rem2 != 0) { int p = (int)(16 - rem2); fs.Write(pad, 0, p); pos += p; }

            Span<byte> footer = stackalloc byte[G0sArchive.FooterSize];
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(0, 4), table.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(4, 4), G0sArchive.FooterMagic1);
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(8, 4), (int)entryBlockOffset);
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(12, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(16, 4), G0sArchive.FooterSize);
            fs.Write(footer);
        }

        File.Move(tmp, finalPath, true);
    }
}
