// Decode an arbitrary byte range of a QAR entry
using System.Buffers.Binary;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Reads plaintext[start, start+len) of a QAR entry without decoding the rest.
/// Needed for containers whose index is INTERLEAVED with their data — a .pftxs
/// walks group header → piece table → blobs → next group header, so its index
/// can't be covered by a prefix, only by hopping between the group headers.
///
/// Cheap only for uncompressed entries, which is the case that matters: the packer
/// zlib-compresses .fpk/.fpkd and nothing else, so .pftxs entries are stored plain.
/// Decrypt1 keys each 8-byte block off its own absolute offset, so decoding can
/// start mid-entry. Decrypt2 is a sequential keystream, so it can't be skipped —
/// but the key can be wound forward with arithmetic alone, no IO and no decode.
/// A compressed entry has no random access at all (zlib is a stream), so it falls
/// back to inflating and discarding up to `start`.
///
/// Same deliberate clone as <see cref="QarPrefixReader"/>: Tools.Qar is untouched,
/// and StreamingTests diffs the result against a full decode.
/// </summary>
public static class QarRangeReader
{
    public static byte[] Read(QarEntry e, Stream source, long start, int len)
    {
        if (len <= 0 || start < 0) return Array.Empty<byte>();
        int dh = e.DataHeader.EncryptionMagic > 0
            ? QarConstants.GetDataHeaderSize(e.DataHeader.EncryptionMagic) : 0;
        int stored = (int)Math.Max(e.Header.UncompressedSize, e.Header.CompressedSize);
        long plainLen = stored - dh;
        if (start >= plainLen) return Array.Empty<byte>();
        if (start + len > plainLen) len = (int)(plainLen - start);

        if (e.Header.Compressed)
        {
            // No random access into a zlib stream — take the prefix and slice.
            var pre = QarPrefixReader.Read(e, source, checked((int)(start + len)));
            if (pre.Length <= start) return Array.Empty<byte>();
            int have = (int)Math.Min(len, pre.Length - start);
            return pre[(int)start..((int)start + have)];
        }

        // Buffer offsets: plaintext byte P sits at dh + P inside the decrypted entry.
        long bufStart = dh + start;
        long alignedStart = bufStart & ~7L;                    // Decrypt1 works in 8-byte blocks
        int skew = (int)(bufStart - alignedStart);
        int span = Align8(skew + len);
        if (alignedStart + span > stored) span = Align8((int)(stored - alignedStart));

        source.Position = e.Header.DataOffset + alignedStart;
        var buf = ReadExact(source, span);
        if (buf.Length == 0) return Array.Empty<byte>();

        Decrypt1From(buf, e.Header.Md5Sum, e.Header.PathHash, e.Header.Version, alignedStart);

        if (dh > 0)
        {
            // Wind the Decrypt2 keystream to this offset, then apply it.
            long words = (alignedStart - dh) / 4;
            if (alignedStart < dh) return Array.Empty<byte>();   // range starts inside the header
            Decrypt2From(buf, e.DataHeader.Key, words);
        }

        int take = Math.Min(len, buf.Length - skew);
        return take <= 0 ? Array.Empty<byte>() : buf[skew..(skew + take)];
    }

    private static int Align8(int n) => (n + 7) & ~7;

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
        return n == count ? buf : buf[..(n & ~7)];
    }

    // Decrypt1 with an explicit starting offset (the original's Position).
    private static void Decrypt1From(byte[] data, byte[] md5sum, ulong pathHash, uint version, long position)
    {
        uint hashLow = (uint)(pathHash & 0xFFFFFFFFu);
        int md5Offset = (int)(hashLow % 2) * 8;
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(md5sum.AsSpan(md5Offset, 8));
        uint seedLow = (uint)(seed & 0xFFFFFFFFu);
        uint seedHigh = (uint)(seed >> 32);
        var table = QarConstants.DecryptionTable;

        for (int i = 0; i < data.Length / 8; i++)
        {
            int off1 = i * 8, off2 = off1 + 4;
            long abs = off1 + position;
            int index = version == 2
                ? 2 * (int)(((ulong)hashLow + seed + (ulong)(abs / 11)) % 4)
                : 2 * (int)(((ulong)hashLow + (ulong)(abs / 11)) % 4);

            uint u1 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off1, 4)) ^ table[index];
            uint u2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off2, 4)) ^ table[index + 1];
            if (version == 2) { u1 ^= seedLow; u2 ^= seedHigh; }
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off1, 4), u1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off2, 4), u2);
        }
    }

    // Advance the keystream `skipWords` 4-byte words, then decrypt in place.
    private static void Decrypt2From(byte[] data, uint key, long skipWords)
    {
        uint k = key * 278u;
        uint blockKey = key | ((key ^ 25974u) << 16);
        for (long w = 0; w < skipWords; w++) blockKey = k + 48828125u * blockKey;

        for (int p = 0; p + 4 <= data.Length; p += 4)
        {
            uint x = blockKey ^ BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p, 4), x);
            blockKey = k + 48828125u * blockKey;
        }
    }
}
