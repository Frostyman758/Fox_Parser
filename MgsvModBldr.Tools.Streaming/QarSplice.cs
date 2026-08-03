// Rewrite a QAR, copying unchanged blocks verbatim
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Splice a .dat/.qar in place: every untouched entry is streamed VERBATIM out of the
/// old file (no decode, no recompress), the supplied entries are written from their
/// pre-encoded blocks, and hashes the archive didn't have are appended at the end.
/// The archive is written to a sibling temp file and moved over the original.
/// </summary>
public static class QarSplice
{
    /// <summary>blocks: hash → an already-encoded QAR block (see <see cref="QarEncode"/>).</summary>
    public static void Apply(string datPath, QarReader qr, IReadOnlyDictionary<ulong, byte[]> blocks)
    {
        var pending = new Dictionary<ulong, byte[]>(blocks);
        var prepared = new List<PreparedBlock>(qr.Entries.Count + pending.Count);
        foreach (var e in qr.Entries)
        {
            if (pending.Remove(e.Header.PathHash, out var nb))
                prepared.Add(PreparedBlock.FromBytes(e.Header.PathHash, nb));
            else
            {
                var (off, len) = qr.BlockExtent(e);
                prepared.Add(PreparedBlock.FromSource(e.Header.PathHash, datPath, off, len));
            }
        }
        foreach (var (h, nb) in pending) prepared.Add(PreparedBlock.FromBytes(h, nb));

        var tmp = datPath + ".qartmp";
        QarBlockWriter.Write(tmp, qr.Flags, qr.Version, prepared);
        File.Move(tmp, datPath, overwrite: true);
    }

    /// <summary>Drop entries by hash; everything else streams through untouched.</summary>
    public static void Remove(string datPath, QarReader qr, IReadOnlyCollection<ulong> hashes)
    {
        var prepared = new List<PreparedBlock>(qr.Entries.Count);
        foreach (var e in qr.Entries)
        {
            if (hashes.Contains(e.Header.PathHash)) continue;
            var (off, len) = qr.BlockExtent(e);
            prepared.Add(PreparedBlock.FromSource(e.Header.PathHash, datPath, off, len));
        }
        var tmp = datPath + ".qartmp";
        QarBlockWriter.Write(tmp, qr.Flags, qr.Version, prepared);
        File.Move(tmp, datPath, overwrite: true);
    }

    /// <summary>
    /// Replace one entry's plaintext, keeping the original entry's hash, stored path
    /// and compression flag (so hash-only entries stay addressable).
    /// </summary>
    public static void Replace(string datPath, string entryPath, byte[] plaintext)
    {
        var qr = new QarReader(datPath);
        var target = qr.Find(entryPath) ?? throw new FileNotFoundException($"{entryPath} not in {Path.GetFileName(datPath)}");
        var block = QarEncode.EncodeBlock(
            string.IsNullOrEmpty(target.Header.FilePath) ? Hashing.ToQarPath(entryPath) : target.Header.FilePath,
            target.Header.PathHash, plaintext, target.Header.Compressed, qr.Version);
        Apply(datPath, qr, new Dictionary<ulong, byte[]> { [target.Header.PathHash] = block });
    }

    /// <summary>Add (or overwrite) an entry by game path.</summary>
    public static void Add(string datPath, string entryPath, byte[] plaintext)
    {
        var qr = new QarReader(datPath);
        ulong hash = Hashing.NameToHash(entryPath);
        var existing = qr.Find(hash);
        bool compressed = existing?.Header.Compressed ?? QarEncode.ShouldCompress(entryPath);
        var block = QarEncode.EncodeBlock(Hashing.ToQarPath(entryPath), hash, plaintext, compressed, qr.Version);
        Apply(datPath, qr, new Dictionary<ulong, byte[]> { [hash] = block });
    }
}
