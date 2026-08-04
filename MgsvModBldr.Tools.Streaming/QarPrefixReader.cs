// Decode only the head of a QAR entry
using System.Buffers.Binary;
using System.IO.Compression;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Reads the FIRST n plaintext bytes of a QAR entry — enough to parse a nested
/// pack's index without inflating its whole payload (an fpk stores header, entry
/// table and path strings before its file data).
///
/// DELIBERATE CLONE. MgsvModBldr.Tools.Qar is byte-exact against datfpk and every
/// other tool sits on it, so it is not modified to add this: a bug here can only
/// break Streaming, never FoxBrowser / FastBite / the shell extension. Only the
/// two cipher passes are duplicated (they're internal to Tools.Qar); the constants,
/// entry headers and table parsing all still come from Tools.Qar.
///
/// Correctness is not assumed — StreamingTests diffs this against the real
/// QarEntry.ReadData() over live archive entries, so drift shows up as a failure.
///
/// Prefix decoding is sound because both passes are forward-only keystreams from
/// offset 0: Decrypt1 keys each 8-byte block off its own absolute offset, Decrypt2
/// advances its key once per 4-byte word. An 8-aligned prefix therefore produces
/// exactly the bytes a full read would.
/// </summary>
public static class QarPrefixReader
{
    /// <summary>Plaintext prefix of <paramref name="e"/>, up to <paramref name="want"/> bytes.</summary>
    public static byte[] Read(QarEntry e, Stream source, int want)
    {
        if (want <= 0) return Array.Empty<byte>();
        int stored = (int)Math.Max(e.Header.UncompressedSize, e.Header.CompressedSize);
        if (stored <= 0) return Array.Empty<byte>();

        // An encrypted entry carries a data header inside the stored bytes, so the
        // window has to cover that too or the payload comes up short.
        int need = Align8(want + (e.DataHeader.EncryptionMagic > 0
            ? QarConstants.GetDataHeaderSize(e.DataHeader.EncryptionMagic) : 0));

        if (!e.Header.Compressed)
            return Decode(e, source, Math.Min(stored, need), want);

        // Compressed: the inflate ratio is unknown, so widen the ciphertext window
        // until enough plaintext comes out (or the entry is exhausted).
        for (int grab = Math.Min(stored, Math.Max(need, 1 << 16)); ; grab = Math.Min(stored, grab * 4))
        {
            var got = Decode(e, source, grab, want);
            if (got.Length >= want || grab >= stored) return got;
        }
    }

    private static int Align8(int n) => (n + 7) & ~7;

    private static byte[] Decode(QarEntry e, Stream source, int grab, int want)
    {
        source.Position = e.Header.DataOffset;
        var buf = ReadExact(source, Align8(grab));
        if (buf.Length == 0) return Array.Empty<byte>();

        Decrypt1(buf, e.Header.Md5Sum, e.Header.PathHash, e.Header.Version);

        // The 8-byte read alignment can overshoot the entry; the payload ends where
        // the stored size says, and Decrypt2 must not run past it — it only ever
        // covers whole 4-byte words, leaving up to 3 trailing bytes as Decrypt1 left
        // them. Overshooting consumes those bytes as a word and corrupts the tail.
        int stored = (int)Math.Max(e.Header.UncompressedSize, e.Header.CompressedSize);
        int from = 0;
        if (e.DataHeader.EncryptionMagic > 0)
        {
            int dhSize = QarConstants.GetDataHeaderSize(e.DataHeader.EncryptionMagic);
            if (buf.Length <= dhSize) return Array.Empty<byte>();
            Decrypt2(buf, dhSize, Math.Min(buf.Length, stored), e.DataHeader.Key);
            from = dhSize;
        }
        int end = Math.Min(buf.Length, stored);

        if (!e.Header.Compressed)
        {
            int n0 = Math.Min(want, end - from);
            return n0 <= 0 ? Array.Empty<byte>() : buf[from..(from + n0)];
        }

        // A truncated zlib window throws at the tail — keep what inflated cleanly.
        using var src = new MemoryStream(buf, from, end - from);
        using var zl = new ZLibStream(src, CompressionMode.Decompress);
        var outBuf = new byte[want];
        int n = 0;
        try
        {
            while (n < want)
            {
                int r = zl.Read(outBuf, n, want - n);
                if (r == 0) break;
                n += r;
            }
        }
        catch (InvalidDataException) { /* ran off the end of the partial window */ }
        return n == want ? outBuf : outBuf[..n];
    }

    private static byte[] ReadExact(Stream s, int count)
    {
        var buf = new byte[count];
        int n = 0;
        while (n < count)
        {
            int r = s.Read(buf, n, count - n);
            if (r == 0) break;
            n += r;
        }
        return n == count ? buf : buf[..(n & ~7)];   // keep whole 8-byte blocks
    }

    // Mirrors Tools.Qar Decrypt1Stream at Position 0. Whole 8-byte blocks only,
    // so the tail remainder path of the original is never needed here.
    private static void Decrypt1(byte[] data, byte[] md5sum, ulong pathHash, uint version)
    {
        uint hashLow = (uint)(pathHash & 0xFFFFFFFFu);
        int md5Offset = (int)(hashLow % 2) * 8;
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(md5sum.AsSpan(md5Offset, 8));
        uint seedLow = (uint)(seed & 0xFFFFFFFFu);
        uint seedHigh = (uint)(seed >> 32);
        var table = QarConstants.DecryptionTable;
        int blocks = data.Length / 8;

        for (int i = 0; i < blocks; i++)
        {
            int off1 = i * 8, off2 = off1 + 4;
            int index = version == 2
                ? 2 * (int)(((ulong)hashLow + seed + (ulong)(off1 / 11)) % 4)
                : 2 * (int)(((ulong)hashLow + (ulong)(off1 / 11)) % 4);

            uint u1 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off1, 4)) ^ table[index];
            uint u2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off2, 4)) ^ table[index + 1];
            if (version == 2) { u1 ^= seedLow; u2 ^= seedHigh; }
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off1, 4), u1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off2, 4), u2);
        }
    }

    // Mirrors Tools.Qar Decrypt2Stream: one key advance per 4-byte word, in place,
    // stopping at `end` — a trailing 1-3 bytes are left exactly as they were.
    private static void Decrypt2(byte[] data, int from, int end, uint key)
    {
        uint k = key * 278u;
        uint blockKey = key | ((key ^ 25974u) << 16);
        for (int p = from; p + 4 <= end; p += 4)
        {
            uint x = blockKey ^ BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p, 4), x);
            blockKey = k + 48828125u * blockKey;
        }
    }
}
