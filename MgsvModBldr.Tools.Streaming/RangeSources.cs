// Range delegates over archive entries
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Adapters between the archive readers here and Tools.Index, whose readers take
/// a plain byte-range delegate. This is the seam that lets one index reader serve
/// a container sitting in a .dat, in a .g0s, nested inside another pack, or loose.
/// </summary>
public static class RangeSources
{
    /// <summary>Plaintext of a QAR entry. Cheap for stored entries, inflate-and-slice for zlib ones.</summary>
    public static RangeReader ForQar(QarEntry e, Stream source)
        => (off, len) => QarRangeReader.Read(e, source, off, len);

    /// <summary>Plaintext of a .g0s entry (never compressed, so ranges map straight through).</summary>
    public static RangeReader ForG0s(G0sEntry e, Stream source)
        => (off, len) => G0sRangeReader.Read(e, source, off, len);

    /// <summary>A window inside another container — for a pack nested in a pack.</summary>
    public static RangeReader Slice(RangeReader inner, long start, long length)
        => (off, len) =>
        {
            if (off < 0 || len < 0 || off + len > length) return null;
            return inner(start + off, len);
        };

    /// <summary>An in-memory buffer, for content already decoded.</summary>
    public static RangeReader ForBytes(byte[] bytes)
        => (off, len) =>
        {
            if (off < 0 || len < 0 || off + len > bytes.Length) return null;
            var o = new byte[len];
            Array.Copy(bytes, off, o, 0, len);
            return o;
        };

    /// <summary>Plaintext size of a QAR entry (what the index readers bound against).</summary>
    public static long PlainSize(QarEntry e)
    {
        long stored = Math.Max(e.Header.UncompressedSize, e.Header.CompressedSize);
        return e.DataHeader.EncryptionMagic > 0
            ? stored - QarConstants.GetDataHeaderSize(e.DataHeader.EncryptionMagic)
            : stored;
    }

    /// <summary>Plaintext size of a .g0s entry (the inner cipher adds an 8-byte prefix).</summary>
    public static long PlainSize(G0sEntry e, Stream source)
    {
        var head = G0sRangeReader.Read(e, source, 0, 8);
        return head.Length == 0 ? e.Size : e.Size - (IsInner(e, source) ? 8 : 0);
    }

    private static bool IsInner(G0sEntry e, Stream source)
    {
        // G0sRangeReader already strips the prefix, so compare decoded vs raw length.
        var probe = G0sRangeReader.Read(e, source, 0, (int)Math.Min(e.Size, 8));
        return probe.Length > 0 && e.Size >= 8 && G0sArchive.PeekIsInner(RawHead(e, source), e.Offset);
    }

    private static byte[] RawHead(G0sEntry e, Stream source)
    {
        source.Position = 16L * e.Offset;
        var b = new byte[8];
        int n = 0;
        while (n < 8) { int r = source.Read(b, n, 8 - n); if (r == 0) break; n += r; }
        return b;
    }
}
