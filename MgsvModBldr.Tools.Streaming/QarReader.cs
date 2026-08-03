// Lazy QAR index + raw-block reader
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Lazy QAR index + raw-block extractor. Reads only the header + section table
/// (no entry bodies — the cheap part), then pulls a single entry's *raw on-disk
/// block* on demand for verbatim splicing.
///
/// A raw block (32-byte entry header + ciphered/compressed payload) is
/// position-independent: it decodes identically wherever it lands in any QAR, so
/// it can be copied byte-for-byte instead of decode+recompress.
/// </summary>
public sealed class QarReader
{
    private readonly string _path;
    private readonly QarFile _qar = new();

    public uint Flags { get; }
    public uint Version { get; }
    public string Path => _path;
    public IReadOnlyList<QarEntry> Entries => _qar.Entries;

    /// <summary>On-disk [offset, length) of an entry's raw block (for verbatim splice-copy).</summary>
    public (long Offset, int Length) BlockExtent(QarEntry e)
    {
        long start = e.Header.DataOffset - QarFormat.EntryHeaderSize;
        long len = QarFormat.EntryHeaderSize + (long)e.Header.CompressedSize;
        long fileLen = new FileInfo(_path).Length;
        if (start + len > fileLen) len = fileLen - start;
        return (start, (int)len);
    }

    public QarReader(string path)
    {
        _path = path;
        _qar.ReadFrom(path);   // headers only
        _qar.Close();          // release the stream; we reopen per block read
        Flags = _qar.Flags;
        Version = _qar.Version;
    }

    /// <summary>Find an entry by its archive hash.</summary>
    public QarEntry Find(ulong hash)
    {
        foreach (var e in _qar.Entries)
            if (e.Header.PathHash == hash) return e;
        return null;
    }

    /// <summary>Find an entry by game path (hashed), falling back to a stored-path match.</summary>
    public QarEntry Find(string qarPath)
    {
        var e = Find(Hashing.NameToHash(qarPath));
        if (e is not null) return e;
        string want = Hashing.ToQarPath(qarPath);
        foreach (var c in _qar.Entries)
            if (!string.IsNullOrEmpty(c.Header.FilePath)
                && string.Equals(Hashing.ToQarPath(c.Header.FilePath), want, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    /// <summary>Decode an entry to its plaintext bytes (decrypt + decompress) — used as a merge source.</summary>
    public byte[] ReadDecoded(QarEntry e)
    {
        using var fs = File.OpenRead(_path);
        e.ReadData(fs);
        var data = e.Data;
        e.Data = Array.Empty<byte>();
        e.Loaded = false;
        return data;
    }

    /// <summary>The entry's exact on-disk block bytes (header + payload), ready to splice.</summary>
    public byte[] ReadRawBlock(QarEntry e)
    {
        long start = e.Header.DataOffset - QarFormat.EntryHeaderSize;
        long len = QarFormat.EntryHeaderSize + (long)e.Header.CompressedSize;

        using var fs = File.OpenRead(_path);
        if (start + len > fs.Length) len = fs.Length - start; // guard the final entry
        var buf = new byte[len];
        fs.Seek(start, SeekOrigin.Begin);
        int n = 0;
        while (n < buf.Length)
        {
            int r = fs.Read(buf, n, buf.Length - n);
            if (r == 0) throw new EndOfStreamException($"short read for block at {start}");
            n += r;
        }
        return buf;
    }
}
